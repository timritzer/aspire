import * as assert from 'assert';
import { execFileSync } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import type { ProjectLaunchConfiguration } from '../dcp/types';
import { waitForRepositoryIdle } from './helpers/assertions';
import { executeE2eControlCommand, writeFileWithRetry } from './helpers/fixtures';
import { getWorkspaceRoot } from './helpers/paths';
import { openAspireView } from './helpers/vscode';

suite('Aspire coordinated build E2E', function () {
    this.timeout(240000);

    test('uses coordinated Release output without rebuilding the project', async () => {
        await openAspireView();
        await waitForRepositoryIdle();

        const projectDirectory = path.join(getWorkspaceRoot(), 'CoordinatedReleaseProject');
        const projectPath = path.join(projectDirectory, 'CoordinatedReleaseProject.csproj');
        const programPath = path.join(projectDirectory, 'Program.cs');
        fs.mkdirSync(projectDirectory, { recursive: true });

        try {
            writeFileWithRetry(projectPath, `
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
            `);
            writeFileWithRetry(programPath, 'System.Console.WriteLine("coordinated");\n');
            execFileSync('dotnet', ['build', projectPath, '--configuration', 'Release', '--nologo'], {
                cwd: projectDirectory,
                stdio: 'pipe',
            });

            // If the extension ignores suppress_build and launches its own project build, this invalid
            // source makes the E2E command fail. TargetPath evaluation itself does not compile sources.
            writeFileWithRetry(programPath, 'this does not compile\n');
            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath,
                build_configuration: 'Release',
                suppress_build: true,
            };
            const controlStatus = await executeE2eControlCommand({
                name: 'createResourceDebugConfiguration',
                launchConfig,
                debug: false,
                isApphost: true,
            }, { timeoutMs: 180000 });
            const debugConfiguration = controlStatus.result as { program?: string };

            assert.ok(debugConfiguration.program);
            assert.match(debugConfiguration.program.replaceAll('\\', '/'), /\/bin\/Release\//);
        } finally {
            fs.rmSync(projectDirectory, { recursive: true, force: true });
        }
    });
});
