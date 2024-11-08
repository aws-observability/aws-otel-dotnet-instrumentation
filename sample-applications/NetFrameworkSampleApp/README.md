# Http Server Net Framework App

This is a simple http server app written running on net48 (.Net Framework). 

# Building

To build the app, you will need VS2022. 
1. Open VS2022
2. Choose open a project or solution
3. Choose the HttpServer.sln in this directory
4. You can then start the app using IIS Express

# Testing with Auto Instrumentation

1. To test with auto instrumentation, you will first need to build the auto instrumentation folder (OpenTelemetryDistribution) using the `build.ps1` script.
2. In you powershell instance, you can either set the environment variables manually or using the installation script under `projectRoot\bi\InstallationScripts\AWS.Otel.DotNet.Auto.psm1`. You can run `Import-Module <psm1_path>` and then `Register-OpenTelemetryForCurrentSession -OTelServiceName "MyServiceDisplayName"`.
3. Finally, in the same powershell session, navigate to where IIS Express is saved (`C:\Program Files\IIS Express>`) and then run ` .\iisexpress.exe /path:"path\to\distro\sample-applications\NetFrameworkSampleApp\HttpServer"`. Make sure you built the project first before running it. 
4. You can make requests to the `localhost:port/` which will show a asp.net landing page or `localhost:port/outgoing-http-call/test/{id}` where id is just any random number. This will make a call to aws.amazon.com and can be used to test parent-child span relationship.


