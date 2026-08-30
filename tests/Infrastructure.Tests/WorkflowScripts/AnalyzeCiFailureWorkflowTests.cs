// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class AnalyzeCiFailureWorkflowTests(ITestOutputHelper output) : IDisposable
{
    private const string ValidationScriptRelativePath = ".github/workflows/analyze-ci-failure-validation.sh";

    private static readonly string s_sourceWorkflow = ReadWorkflow("analyze-ci-failure.md");
    private static readonly string s_validationScript = File.ReadAllText(
        Path.Combine(RepoRoot.Path, ValidationScriptRelativePath));

    private static readonly string[] s_executableWorkflows =
    [
        s_sourceWorkflow,
        ReadWorkflow("analyze-ci-failure.lock.yml"),
    ];

    private readonly TemporaryWorkspace _workspace = TemporaryWorkspace.Create(output);

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void RunScopeComesFromAnalyzedRunMetadata()
    {
        ForEachExecutableWorkflow(workflow =>
        {
            Assert.Contains("RUN_EVENT=$(jq -r '.event // \"\"' ci-failure-data/run.json)", workflow, StringComparison.Ordinal);
            Assert.Contains("case \"${RUN_EVENT}:${HEAD_BRANCH}\" in", workflow, StringComparison.Ordinal);
            Assert.Contains("push:main)", workflow, StringComparison.Ordinal);
            Assert.Contains("pull_request:*|pull_request_target:*)", workflow, StringComparison.Ordinal);
            Assert.Contains("RUN_SCOPE=\"main\"", workflow, StringComparison.Ordinal);
            Assert.Contains("RUN_SCOPE=\"pull-request\"", workflow, StringComparison.Ordinal);
            var scopeCase = GetSection(workflow, "case \"${RUN_EVENT}:${HEAD_BRANCH}\" in", "esac");
            Assert.Contains(
                "*)\necho \"::notice::Unsupported run scope: event=${RUN_EVENT}, branch=${HEAD_BRANCH}. Skipping analysis.\"\necho \"has_work=false\" >> \"$GITHUB_OUTPUT\"\nexit 0",
                scopeCase,
                StringComparison.Ordinal);
            Assert.Contains("run_scope: $run_scope", workflow, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void MainRunContextTreatsTriggeringMergeAsNonCausal()
    {
        ForEachExecutableWorkflow(workflow =>
        {
            Assert.Contains("last-successful-main-run.json", workflow, StringComparison.Ordinal);
            Assert.Contains("candidate-merges.json", workflow, StringComparison.Ordinal);
            Assert.Contains(
                "Unable to find the last successful main run. Continuing without a candidate merge range.",
                workflow,
                StringComparison.Ordinal);
            Assert.Contains("candidate-merge-history-status.json", workflow, StringComparison.Ordinal);
            Assert.Contains("Candidate merge history is unavailable.", workflow, StringComparison.Ordinal);
            Assert.Contains("Candidate merge history is incomplete.", workflow, StringComparison.Ordinal);
            Assert.Contains(
                "gh api --paginate --slurp \"repos/${REPO}/compare/${LAST_SUCCESSFUL_SHA}...${HEAD_SHA}?per_page=100\"",
                workflow,
                StringComparison.Ordinal);
            Assert.Contains("RECEIVED_COMMIT_COUNT", workflow, StringComparison.Ordinal);
            Assert.Contains("TOTAL_COMMIT_COUNT", workflow, StringComparison.Ordinal);
            Assert.Contains(
                "Triggering merge PR (context only, not necessarily causal)",
                workflow,
                StringComparison.Ordinal);
        });
        Assert.Contains(
            "consider the complete candidate merge range since the last successful main run",
            s_sourceWorkflow,
            StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["bash", "jq"])]
    public async Task AnalysisValidatorRejectsMismatchedTrustedScope()
    {
        await WriteValidationFixtureAsync(
            """{"run_id":123,"run_scope":"pull-request","verdict":"code-issue","pr":42,"failed_jobs":[],"causes":[]}""",
            """{"run_id":123,"run_scope":"main"}""",
            "[]");

        var result = await RunValidationScriptAsync(Path.Combine(_workspace.Path, "output.json"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "::error::Analysis result does not match trusted run context",
            result.Output,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        """{"run_id":123,"run_scope":"main","verdict":"main-repository-breakage","pr":42,"failed_jobs":[],"causes":[]}""",
        """{"run_id":123,"run_scope":"main"}""",
        "[]",
        "::error::Main run analysis must not identify a subject PR")]
    [InlineData(
        """{"run_id":123,"run_scope":"pull-request","verdict":"code-issue","pr":42,"failed_jobs":[{"id":456,"classification":"code-issue"}],"causes":[]}""",
        """{"run_id":123,"run_scope":"pull-request"}""",
        """[{"id":123,"name":"Tests"}]""",
        "::error::Analysis failed-job IDs do not match the trusted failed jobs")]
    [InlineData(
        """{"run_id":123,"run_scope":"pull-request","verdict":"transient-infra","pr":42,"failed_jobs":[{"id":123,"classification":"transient-infra"}],"causes":[]}""",
        """{"run_id":123,"run_scope":"pull-request"}""",
        """[{"id":123,"name":"Tests"}]""",
        "::error::A transient-infra verdict requires every failed job and cause to be an infrastructure failure")]
    [RequiresTools(["bash", "jq"])]
    public async Task AnalysisValidatorRejectsUntrustedAssociations(
        string analysis,
        string runContext,
        string trustedFailedJobs,
        string expectedError)
    {
        await WriteValidationFixtureAsync(analysis, runContext, trustedFailedJobs);

        var result = await RunValidationScriptAsync(Path.Combine(_workspace.Path, "output.json"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expectedError, result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void MainRepositoryBreakageUsesDedicatedIssueAndNeverPrComment()
    {
        Assert.Contains(
            "Deterministic compilation, test, API compatibility, lint, or formatting failures are `main-repository-breakage`",
            s_sourceWorkflow,
            StringComparison.Ordinal);

        ForEachExecutableWorkflow(workflow =>
        {
            Assert.Contains("CAUSE_TYPE\" = \"main-repository-breakage", workflow, StringComparison.Ordinal);
            Assert.Contains("LABELS=\"ci-failure-cause,main-ci-break\"", workflow, StringComparison.Ordinal);
            Assert.Contains("ISSUE_TITLE=$(jq -r '\"[Main CI Failure] \" + .title'", workflow, StringComparison.Ordinal);
            Assert.Contains(
                "if [ \"$RUN_SCOPE\" = \"main\" ]; then\necho \"Main run analysis is reported through cause issues, not PR comments.\"\nexit 0",
                workflow,
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public void PublisherValidatesAgentResultAgainstTrustedScope()
    {
        ForEachExecutableWorkflow(workflow =>
        {
            Assert.Contains("sparse-checkout: .github/workflows/analyze-ci-failure-validation.sh", workflow, StringComparison.Ordinal);
            Assert.Contains("run: bash .github/workflows/analyze-ci-failure-validation.sh", workflow, StringComparison.Ordinal);
            var validationIndex = workflow.IndexOf(
                "run: bash .github/workflows/analyze-ci-failure-validation.sh",
                StringComparison.Ordinal);
            var publishStepIndex = workflow.IndexOf(
                "- name: Publish analysis data and comment on PR",
                validationIndex,
                StringComparison.Ordinal);
            Assert.True(validationIndex >= 0 && publishStepIndex > validationIndex);
        });

        var validationScript = NormalizeIndentation(s_validationScript);
        Assert.Contains("RUN_CONTEXT_FILE=\"ci-failure-data/run-context.json\"", validationScript, StringComparison.Ordinal);
        Assert.Contains("ANALYSIS_RUN_SCOPE=$(jq -r '.run_scope' \"$ANALYSIS_FILE\")", validationScript, StringComparison.Ordinal);
        Assert.Contains("Analysis result does not match trusted run context\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("Main run analysis must not identify a subject PR\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("TRUSTED_FAILED_JOBS_FILE=\"ci-failure-data/failed-jobs.json\"", validationScript, StringComparison.Ordinal);
        Assert.Contains("Analysis must contain numeric-ID failed_jobs and string-valued causes arrays\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("Cause ${CAUSE_BASENAME} contains unsupported or publisher-owned fields\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("Analysis failed-job IDs do not match the trusted failed jobs\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("Verdict '${VERDICT}' is not permitted for run scope ${TRUSTED_RUN_SCOPE}\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("type '${CAUSE_TYPE}' is not permitted for run scope ${TRUSTED_RUN_SCOPE}\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("Cause ${CAUSE_BASENAME} cannot change type from '${PRIOR_CAUSE_TYPE}' to '${CAUSE_TYPE}'\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("Cause ${CAUSE_BASENAME} is not referenced by the analysis summary\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("Analysis cause IDs must uniquely match the generated cause files\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("Analysis must classify every failed job with a recognized classification\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("Analysis contains a failed-job classification that is not permitted for run scope ${TRUSTED_RUN_SCOPE}\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("if [ \"$INFRA_JOB_COUNT\" -ne \"$FAILED_JOB_COUNT\" ] ||", validationScript, StringComparison.Ordinal);
        Assert.Contains("A transient-infra verdict requires every failed job and cause to be an infrastructure failure\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("if [ \"$FLAKY_JOB_COUNT\" -eq 0 ] || [ \"$TRANSIENT_JOB_COUNT\" -ne \"$FAILED_JOB_COUNT\" ] ||", validationScript, StringComparison.Ordinal);
        Assert.Contains("[ \"$FLAKY_CAUSE_COUNT\" -eq 0 ]", validationScript, StringComparison.Ordinal);
        Assert.Contains("A flaky-test verdict requires at least one flaky job, only transient failed jobs, and only transient causes\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("if [ \"$CODE_ISSUE_JOB_COUNT\" -ne \"$FAILED_JOB_COUNT\" ] || [ \"$CAUSE_COUNT\" -ne 0 ]; then", validationScript, StringComparison.Ordinal);
        Assert.Contains("A code-issue verdict requires every failed job to be a code issue and must not include cause files\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("if [ \"$MAIN_BREAK_JOB_COUNT\" -ne \"$FAILED_JOB_COUNT\" ] ||", validationScript, StringComparison.Ordinal);
        Assert.Contains("A main-repository-breakage verdict requires every failed job and cause to be a main repository breakage\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("if [ \"$MAIN_BREAK_JOB_COUNT\" -eq 0 ] || [ \"$TRANSIENT_JOB_COUNT\" -eq 0 ] ||", validationScript, StringComparison.Ordinal);
        Assert.Contains("A mixed verdict for main requires transient and main-breakage failed jobs and causes\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("if [ \"$CODE_ISSUE_JOB_COUNT\" -eq 0 ] || [ \"$TRANSIENT_JOB_COUNT\" -eq 0 ] || [ \"$CAUSE_COUNT\" -eq 0 ]; then", validationScript, StringComparison.Ordinal);
        Assert.Contains("A mixed verdict for a pull request requires transient and code-issue failed jobs plus a transient cause\"\nexit 1", validationScript, StringComparison.Ordinal);

        Assert.Contains("### If failures include Transient Test Failures and no deterministic failures:", s_sourceWorkflow, StringComparison.Ordinal);
        Assert.Contains("### If ALL failures are Non-Transient PR Code Issues:", s_sourceWorkflow, StringComparison.Ordinal);
        Assert.Contains("### If ALL failures are Main Repository Breakages:", s_sourceWorkflow, StringComparison.Ordinal);
        Assert.Contains(
            "Use `\"transient-infra\"` when every failed job is an infrastructure issue, `\"flaky-test\"` when at least one failed job is a flaky test and every failed job is transient",
            s_sourceWorkflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "`failed_jobs` MUST contain exactly one object for every failed job in the summary, using its exact numeric ID, with no additions, omissions, or duplicates.",
            s_sourceWorkflow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PublisherUsesTrustedMetadataAndVerifiesStoredIssueIdentity()
    {
        ForEachExecutableWorkflow(workflow =>
        {
            var publisher = GetSection(
                workflow,
                "RUN_CONTEXT_FILE=\"ci-failure-data/run-context.json\"",
                "# ── 4. Post PR comment using the analysis JSON ──");

            Assert.Contains("RUN_ID=\"$TRUSTED_RUN_ID\"", publisher, StringComparison.Ordinal);
            Assert.Contains("RUN_SCOPE=\"$TRUSTED_RUN_SCOPE\"", publisher, StringComparison.Ordinal);
            Assert.Contains("PR_NUMBERS=\"$TRUSTED_PR_NUMBERS\"", publisher, StringComparison.Ordinal);
            Assert.Contains("RUN_URL=$(jq -r '.html_url // \"\"' ci-failure-data/run.json)", publisher, StringComparison.Ordinal);
            Assert.Contains("ANALYZED_AT=$(date -u +\"%Y-%m-%dT%H:%M:%SZ\")", publisher, StringComparison.Ordinal);
            Assert.Contains("FIRST_JOB=$(jq -r '.[0].name // \"unknown\"' \"$TRUSTED_FAILED_JOBS_FILE\")", publisher, StringComparison.Ordinal);
            Assert.Contains("FAILED_SHA=$(jq -r '.head_sha // \"unknown\"' \"$RUN_CONTEXT_FILE\")", publisher, StringComparison.Ordinal);
            Assert.Contains("LAST_SUCCESSFUL_SHA=$(jq -r '.head_sha // \"unknown\"' ci-failure-data/last-successful-main-run.json)", publisher, StringComparison.Ordinal);
            Assert.Contains("TRIGGERING_MERGE=$(jq -r 'if .number then \"#\\(.number) \\(.title)\" else \"Not found\" end' ci-failure-data/triggering-merge-pr.json)", publisher, StringComparison.Ordinal);
            Assert.Contains("($new | del(.occurrences, .issue_url))", publisher, StringComparison.Ordinal);
            Assert.Contains("if $ex.issue_url then {issue_url: $ex.issue_url} else {} end", publisher, StringComparison.Ordinal);
            Assert.Contains(
                "Stored cause ${CAUSE_BASENAME} cannot change type from '${CURRENT_CAUSE_TYPE}' to '${CAUSE_TYPE}'\"\nexit 1",
                publisher,
                StringComparison.Ordinal);
            var causeTypeIndex = publisher.IndexOf("CAUSE_TYPE=$(jq -r '.type' \"$CAUSE_FILE\")", StringComparison.Ordinal);
            var currentCauseTypeIndex = publisher.IndexOf("CURRENT_CAUSE_TYPE=$(jq -r '.type // \"\"' \"$EXISTING\")", StringComparison.Ordinal);
            Assert.True(causeTypeIndex >= 0 && causeTypeIndex < currentCauseTypeIndex);
            Assert.Contains("\"$STORED_ISSUE_URL\" =~ ^https://github\\.com/${REPO}/issues/([0-9]+)$", publisher, StringComparison.Ordinal);
            Assert.Contains(".pull_request == null", publisher, StringComparison.Ordinal);
            Assert.Contains("any(.labels[]?; .name == \"ci-failure-cause\")", publisher, StringComparison.Ordinal);
            Assert.Contains("TYPE_MARKER=\"<!-- ci-failure-cause-type:${CAUSE_TYPE} -->\"", publisher, StringComparison.Ordinal);
            Assert.Contains("map(rtrimstr(\"\\r\"))", publisher, StringComparison.Ordinal);
            Assert.Contains("$lines[0] == $marker", publisher, StringComparison.Ordinal);
            Assert.Contains("$lines[1] == $type_marker", publisher, StringComparison.Ordinal);
            Assert.Contains("[\"**Type**: \" + $cause_type]", publisher, StringComparison.Ordinal);
            Assert.True(
                publisher.IndexOf("git -C memory-repo push origin \"HEAD:$MEMORY_BRANCH\"", StringComparison.Ordinal) <
                publisher.IndexOf("# ── 2. Create or update issues for each cause ──", StringComparison.Ordinal));
            Assert.Contains("jq -r --arg run_url \"$RUN_URL\"", workflow, StringComparison.Ordinal);
            Assert.Contains(".user.login == \\\"github-actions[bot]\\\"", workflow, StringComparison.Ordinal);
            Assert.Contains("startswith(\\\"${MARKER}\\\\n\\\")", workflow, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void RerunUsesTrustedRunContext()
    {
        ForEachExecutableWorkflow(workflow =>
        {
            Assert.Contains("const trustedRunId = Number(runContext.run_id);", workflow, StringComparison.Ordinal);
            Assert.Contains("const trustedRunAttempt = Number(runContext.run_attempt);", workflow, StringComparison.Ordinal);
            Assert.Contains("requestedRunId !== trustedRunId", workflow, StringComparison.Ordinal);
            Assert.Contains("analysis.verdict !== 'transient-infra'", workflow, StringComparison.Ordinal);
            Assert.Contains("if (trustedRunScope === 'pull-request')", workflow, StringComparison.Ordinal);
            Assert.Contains("run_id: trustedRunId", workflow, StringComparison.Ordinal);
            Assert.Contains("currentRun.run_attempt !== trustedRunAttempt", workflow, StringComparison.Ordinal);

            var rerunValidation = GetSection(
                workflow,
                "const analysisFile = path.join(path.dirname(outputFile), 'agent', 'analysis-result.json');",
                "if (!enableRerun)");
            Assert.Contains("const causesDir = path.join(path.dirname(outputFile), 'agent', 'causes');", rerunValidation, StringComparison.Ordinal);
            Assert.Contains("const trustedFailedJobsFile = path.join('ci-failure-data', 'failed-jobs.json');", rerunValidation, StringComparison.Ordinal);
            Assert.Contains("analysisJobIdSet.size !== trustedJobIdSet.size", rerunValidation, StringComparison.Ordinal);
            Assert.Contains("!analysisJobIds.every(jobId => trustedJobIdSet.has(jobId))", rerunValidation, StringComparison.Ordinal);
            Assert.Contains("core.setFailed('Rerun requires unique analysis cause IDs matching the generated cause files');\nreturn;", rerunValidation, StringComparison.Ordinal);
            Assert.Contains("cause.type !== 'infra-failure'", rerunValidation, StringComparison.Ordinal);
            Assert.Contains("!summaryCauseIds.includes(causeId)", rerunValidation, StringComparison.Ordinal);
            Assert.Contains("!analysis.failed_jobs.every(job => job && job.classification === 'transient-infra')", rerunValidation, StringComparison.Ordinal);
        });
    }

    private static void ForEachExecutableWorkflow(Action<string> assertion)
    {
        foreach (var workflow in s_executableWorkflows)
        {
            assertion(NormalizeIndentation(workflow));
        }
    }

    private static string NormalizeIndentation(string value)
        => string.Join('\n', value.ReplaceLineEndings("\n").Split('\n').Select(line => line.TrimStart()));

    private static string GetSection(string value, string start, string end)
    {
        var startIndex = value.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Could not find section start: {start}");
        var endIndex = value.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(endIndex >= 0, $"Could not find section end: {end}");
        return value[startIndex..(endIndex + end.Length)];
    }

    private static string ReadWorkflow(string fileName)
        => File.ReadAllText(Path.Combine(RepoRoot.Path, ".github", "workflows", fileName));

    private async Task<CommandResult> RunValidationScriptAsync(string agentOutputPath)
    {
        var scriptPath = Path.Combine(RepoRoot.Path, ValidationScriptRelativePath);
        Assert.True(File.Exists(scriptPath), $"Expected validation helper at '{ValidationScriptRelativePath}'.");

        using var process = new Process();
        process.StartInfo.FileName = "bash";
        process.StartInfo.ArgumentList.Add(scriptPath);
        process.StartInfo.WorkingDirectory = _workspace.Path;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.Environment["GH_AW_AGENT_OUTPUT"] = agentOutputPath;

        process.Start();

        // Read both streams concurrently to avoid deadlock when the validator emits diagnostics.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);

        return new CommandResult(process.ExitCode, await stdoutTask + await stderrTask);
    }

    private async Task WriteValidationFixtureAsync(string analysis, string runContext, string trustedFailedJobs)
    {
        var agentDirectory = Path.Combine(_workspace.Path, "agent");
        var failureDataDirectory = Path.Combine(_workspace.Path, "ci-failure-data");
        Directory.CreateDirectory(agentDirectory);
        Directory.CreateDirectory(failureDataDirectory);

        await File.WriteAllTextAsync(Path.Combine(agentDirectory, "analysis-result.json"), analysis);
        await File.WriteAllTextAsync(Path.Combine(failureDataDirectory, "run-context.json"), runContext);
        await File.WriteAllTextAsync(Path.Combine(failureDataDirectory, "failed-jobs.json"), trustedFailedJobs);
    }

    private sealed record CommandResult(int ExitCode, string Output);
}
