// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.NuGet;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

internal partial class Build
{
    private readonly AbsolutePath nuGetPackageFolder = NukeBuild.RootDirectory / "nupkgs" / "output";

    private Target BuildNuGetPackage => _ => _
        .Executes(() =>
        {
            var project = this.solution.AllProjects.First(project => project.Name == "AWS.Distro.OpenTelemetry.AutoInstrumentation");

            DotNetPack(s => s
                .SetProject(project)
                .SetConfiguration(this.configuration)
                .SetOutputDirectory(this.nuGetPackageFolder));
        });
}
