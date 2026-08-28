// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Infrastructure.Tests;

public sealed class AnalyzeCiFailureWorkflowTests
{
    private static readonly string s_sourceWorkflow = ReadWorkflow("analyze-ci-failure.md");

    private static readonly string[] s_executableWorkflows =
    [
        s_sourceWorkflow,
        ReadWorkflow("analyze-ci-failure.lock.yml"),
    ];

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
            Assert.Contains("echo \"has_work=false\" >> \"$GITHUB_OUTPUT\"\nexit 0", workflow, StringComparison.Ordinal);
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
            Assert.Contains("RUN_CONTEXT_FILE=\"ci-failure-data/run-context.json\"", workflow, StringComparison.Ordinal);
            Assert.Contains("ANALYSIS_RUN_SCOPE=$(jq -r '.run_scope' \"$ANALYSIS_FILE\")", workflow, StringComparison.Ordinal);
            Assert.Contains("Analysis result does not match trusted run context\"\nexit 1", workflow, StringComparison.Ordinal);
            Assert.Contains("Main run analysis must not identify a subject PR or use the PR code-issue verdict\"\nexit 1", workflow, StringComparison.Ordinal);
            Assert.Contains("Pull request run analysis cannot use the main repository breakage verdict\"\nexit 1", workflow, StringComparison.Ordinal);
            Assert.Contains("type '${CAUSE_TYPE}' is not permitted for run scope ${TRUSTED_RUN_SCOPE}\"\nexit 1", workflow, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void RerunUsesTrustedRunContext()
    {
        ForEachExecutableWorkflow(workflow =>
        {
            Assert.Contains("const trustedRunId = Number(runContext.run_id);", workflow, StringComparison.Ordinal);
            Assert.Contains("requestedRunId !== trustedRunId || requestedPrNumbers !== trustedPrNumberText", workflow, StringComparison.Ordinal);
            Assert.Contains("analysis.verdict !== 'transient-infra'", workflow, StringComparison.Ordinal);
            Assert.Contains("if (trustedRunScope === 'pull-request')", workflow, StringComparison.Ordinal);
            Assert.Contains("run_id: trustedRunId", workflow, StringComparison.Ordinal);
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

    private static string ReadWorkflow(string fileName)
        => File.ReadAllText(Path.Combine(RepoRoot.Path, ".github", "workflows", fileName));
}
