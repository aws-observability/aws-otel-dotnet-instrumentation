using System.Runtime.InteropServices;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.NuGet;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
partial class Build
{
    readonly AbsolutePath NuGetPackageFolder = NukeBuild.RootDirectory / "nupkgs" / "output";
    
    Target BuildNuGetPackage => _ => _
        .Executes(() =>
        {
            var project = solution.AllProjects.First(project => project.Name == "AWS.OpenTelemetry.AutoInstrumentation");

            DotNetPack(s => s
                .SetProject(project)
                .SetConfiguration(configuration)
                .SetOutputDirectory(NuGetPackageFolder));
        });
}
