#!/usr/bin/env python3

import os
import re
import subprocess
import sys

def update_file_dependencies(file_path, otel_dotnet_core_version):
    """Update core OpenTelemetry package versions in a .csproj file."""
    try:
        with open(file_path, 'r', encoding='utf-8') as f:
            content = f.read()
        
        updated = False
        
        core_packages = [
            'OpenTelemetry',
            'OpenTelemetry.Api', 
            'OpenTelemetry.Exporter.OpenTelemetryProtocol',
            'OpenTelemetry.Extensions.Propagators'
        ]

        def update_package_version(match):
            nonlocal updated
            package_name = match.group(1)
            current_version = match.group(2)
            
            if package_name in core_packages:
                if current_version != otel_dotnet_core_version:
                    updated = True
                    print(f"Updated {package_name} in {file_path}: {current_version} → {otel_dotnet_core_version}")
                    return f'<PackageReference Include="{package_name}" Version="{otel_dotnet_core_version}" />'
                else:
                    print(f"{package_name} already at latest version: {otel_dotnet_core_version}")
            
            return match.group(0)
        
        pattern = r'<PackageReference\s+Include="([^"]+)"\s+Version="([^"]+)"\s*/?>'
        new_content = re.sub(pattern, update_package_version, content)
        
        if updated:
            with open(file_path, 'w', encoding='utf-8') as f:
                f.write(new_content)
            print(f"Updated {file_path}")
        
        return updated
            
    except OSError as file_error:
        print(f"Error updating {file_path}: {file_error}")
        return False

def update_contrib_packages(csproj_dir):
    """Update contrib packages using dotnet commands."""
    try:
        # First restore the project
        print("Restoring project...")
        restore_result = subprocess.run(
            ['dotnet', 'restore'],
            cwd=csproj_dir,
            capture_output=True,
            text=True,
            check=False
        )
        
        if restore_result.returncode != 0:
            print(f"Failed to restore project: {restore_result.stderr}")
            return False
        
        # Get outdated OpenTelemetry packages
        result = subprocess.run(
            ['dotnet', 'list', 'package', '--outdated'],
            cwd=csproj_dir,
            capture_output=True,
            text=True,
            check=False
        )
        
        if result.returncode != 0:
            print(f"Error running dotnet list: {result.stderr}")
            return False
        
        updated = False
        lines = result.stdout.split('\n')
        
        for line in lines:
            # Parse dotnet list output for OpenTelemetry packages
            if 'OpenTelemetry.' in line and '>' in line and 'Not found' not in line:
                parts = line.split()
                if len(parts) >= 5:  # Ensure we have package name and version
                    package_name = parts[1]
                    latest_version = parts[4]  # Latest version is in 5th column

                    # These versions are shared with the distribution, not selected independently.
                    if package_name in (
                        'OpenTelemetry.AutoInstrumentation',
                        'OpenTelemetry.AutoInstrumentation.PluginApi',
                    ):
                        continue
                    
                    # Update the package
                    update_result = subprocess.run(
                        ['dotnet', 'add', 'package', package_name, '--version', latest_version],
                        cwd=csproj_dir,
                        capture_output=True,
                        text=True,
                        check=False
                    )
                    
                    if update_result.returncode == 0:
                        print(f"Updated {package_name} to {latest_version}")
                        updated = True
                    else:
                        print(f"Failed to update {package_name}: {update_result.stderr}")
        
        return updated
        
    except Exception as e:
        print(f"Error updating contrib packages: {e}")
        return False

def update_instrumentation_versions(csproj_path, build_cs_path, instrumentation_version):
    """Update PluginApi, the pack-only loader, and the distribution to the same release."""
    try:
        if not instrumentation_version:
            return False
        
        version_match = re.fullmatch(r'v?(\d+\.\d+\.\d+)', instrumentation_version)
        if not version_match:
            raise ValueError(f"Invalid automatic instrumentation version: {instrumentation_version}")
        version = version_match.group(1)

        with open(csproj_path, 'r', encoding='utf-8') as input_file:
            csproj_content = input_file.read()
        with open(build_cs_path, 'r', encoding='utf-8') as input_file:
            build_content = input_file.read()

        new_csproj_content = csproj_content
        for package_name in (
            'OpenTelemetry.AutoInstrumentation.PluginApi',
            'OpenTelemetry.AutoInstrumentation',
        ):
            pattern = rf'(<PackageReference\s+Include="{re.escape(package_name)}"\s+Version=")[^"]+(")'
            new_csproj_content, count = re.subn(
                pattern, lambda match: f'{match.group(1)}{version}{match.group(2)}',
                new_csproj_content,
            )
            if count != 1:
                raise ValueError(f"Expected one {package_name} reference in {csproj_path}")

        build_pattern = r'(private const string OpenTelemetryAutoInstrumentationDefaultVersion = ")v[^"]*(";)'
        new_build_content, count = re.subn(
            build_pattern, lambda match: f'{match.group(1)}v{version}{match.group(2)}',
            build_content,
        )
        if count != 1:
            raise ValueError(f"Expected one OpenTelemetryAutoInstrumentationDefaultVersion in {build_cs_path}")

        updated = False
        for file_path, old_content, new_content in (
            (csproj_path, csproj_content, new_csproj_content),
            (build_cs_path, build_content, new_build_content),
        ):
            if new_content != old_content:
                with open(file_path, 'w', encoding='utf-8') as output_file:
                    output_file.write(new_content)
                print(f"Updated automatic instrumentation in {file_path} to {version}")
                updated = True

        return updated
            
    except OSError as file_error:
        print(f"Error updating automatic instrumentation version: {file_error}")
        raise

def main():
    otel_dotnet_core_version = os.environ.get("OTEL_DOTNET_CORE_VERSION")
    otel_dotnet_instrumentation_version = os.environ.get("OTEL_DOTNET_INSTRUMENTATION_VERSION")
    
    if not otel_dotnet_core_version:
        print("Error: OTEL_DOTNET_CORE_VERSION environment variable required")
        sys.exit(1)
    
    if not otel_dotnet_instrumentation_version:
        print("Error: OTEL_DOTNET_INSTRUMENTATION_VERSION environment variable required")
        sys.exit(1)
    
    # All files to update
    files_to_update = [
        'src/AWS.Distro.OpenTelemetry.AutoInstrumentation/AWS.Distro.OpenTelemetry.AutoInstrumentation.csproj',
        'exporters/AWS.Distro.OpenTelemetry.Exporter.Xray.Udp/AWS.Distro.OpenTelemetry.Exporter.Xray.Udp.csproj',
        'test/AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests/AWS.Distro.OpenTelemetry.AutoInstrumentation.Tests.csproj',
        'sample-applications/udp-exporter-test-app/dotnet-sample-app.csproj'
    ]
    
    # Set all three pins before restoring, including the pack-only reference that
    # dotnet list package cannot discover during an ordinary restore.
    instrumentation_updated = update_instrumentation_versions(
        files_to_update[0], 'build/Build.cs', otel_dotnet_instrumentation_version
    )
    
    # Update core packages in all files
    any_core_updated = False
    for file_path in files_to_update:
        if update_file_dependencies(file_path, otel_dotnet_core_version):
            any_core_updated = True
    
    # Update contrib packages in all directories
    any_contrib_updated = False
    for file_path in files_to_update:
        csproj_dir = os.path.dirname(file_path)
        if update_contrib_packages(csproj_dir):
            any_contrib_updated = True
    
    if any_core_updated or any_contrib_updated or instrumentation_updated:
        print(f"Dependencies updated to Core {otel_dotnet_core_version}")
    else:
        print("No updates were made")

if __name__ == '__main__':
    main()
