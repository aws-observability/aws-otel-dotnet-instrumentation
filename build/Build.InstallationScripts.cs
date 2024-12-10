// Copyright Splunk Inc
// SPDX-License-Identifier: Apache-2.0
// Modifications Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.

using Nuke.Common;
using Nuke.Common.IO;

internal partial class Build : NukeBuild
{
    private readonly AbsolutePath installationScriptsFolder = RootDirectory / "bin" / "InstallationScripts";
    private readonly string version = "1.5.0.dev0";

    public Target BuildInstallationScripts => _ => _
        .After(this.Clean)
        .Executes(() =>
        {
            var scriptTemplates = RootDirectory / "script-templates";
            var templateFiles = scriptTemplates.GetFiles();
            foreach (var templateFile in templateFiles)
            {
                var scriptFile = this.installationScriptsFolder / templateFile.Name.Replace(".template", string.Empty);
                FileSystemTasks.CopyFile(templateFile, scriptFile);
                scriptFile.UpdateText(x =>
                    x.Replace("{{VERSION}}", this.version));
            }
        });
}
