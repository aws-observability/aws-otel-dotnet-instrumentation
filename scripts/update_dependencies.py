#!/usr/bin/env python3

import os
import re
import subprocess
import sys

def update_core_packages(file_path, otel_dotnet_core_version):
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
        
        print(f"Checking for core packages to update with version {otel_dotnet_core_version}")
        
        def update_package_version(match):
            nonlocal updated
            package_name = match.group(1)
            current_version = match.group(2)
            
            if package_name in core_packages:
                if current_version != otel_dotnet_core_version:
                    updated = True
                    print(f"Updated {package_name}: {current_version} → {otel_dotnet_core_version}")
                    return f'<PackageReference Include="{package_name}" Version="{otel_dotnet_core_version}" />'
                else:
                    print(f"{package_name} already at latest version: {otel_dotnet_core_version}")
            else:
                print(f"Skipping {package_name} (not in core packages list)")
            
            return match.group(0)
        
        pattern = r'<PackageReference\s+Include="([^"]+)"\s+Version="([^"]+)"\s*/?>'
        new_content = re.sub(pattern, update_package_version, content)
        
        if updated:
            with open(file_path, 'w', encoding='utf-8') as f:
                f.write(new_content)
        
        return updated
            
    except (OSError, IOError) as file_error:
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
        
        print(f"dotnet list package --outdated output:")
        print(result.stdout)
        print(f"Return code: {result.returncode}")
        
        if result.returncode != 0:
            print(f"Error running dotnet list: {result.stderr}")
            return False
        
        updated = False
        lines = result.stdout.split('\n')
        
        print(f"Processing {len(lines)} lines of output")
        
        for i, line in enumerate(lines):
            print(f"Line {i}: '{line.strip()}'")
            # Parse dotnet list output for OpenTelemetry packages
            if 'OpenTelemetry.' in line and '>' in line:
                print(f"Found OpenTelemetry package line: {line}")
                parts = line.split()
                print(f"Split into {len(parts)} parts: {parts}")
                if len(parts) >= 4:
                    package_name = parts[1]
                    latest_version = parts[-1]
                    print(f"Attempting to update {package_name} to {latest_version}")
                    
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

def update_build_cs_file(file_path, instrumentation_version):
    """Update the openTelemetryAutoInstrumentationDefaultVersion in Build.cs."""
    try:
        if not instrumentation_version:
            return False
        
        with open(file_path, 'r', encoding='utf-8') as input_file:
            content = input_file.read()
        
        pattern = r'private const string OpenTelemetryAutoInstrumentationDefaultVersion = "v[^"]*";'
        replacement = f'private const string OpenTelemetryAutoInstrumentationDefaultVersion = "{instrumentation_version}";'
        
        if re.search(pattern, content):
            new_content = re.sub(pattern, replacement, content)
            
            if new_content != content:
                with open(file_path, 'w', encoding='utf-8') as output_file:
                    output_file.write(new_content)
                print(f"Updated OpenTelemetryAutoInstrumentationDefaultVersion to {instrumentation_version}")
                return True
            else:
                print(f"OpenTelemetryAutoInstrumentationDefaultVersion already at latest version: {instrumentation_version}")
        
        return False
            
    except (OSError, IOError) as file_error:
        print(f"Error updating Build.cs: {file_error}")
        return False

def main():
    otel_dotnet_core_version = os.environ.get("OTEL_DOTNET_CORE_VERSION")
    otel_dotnet_instrumentation_version = os.environ.get("OTEL_DOTNET_INSTRUMENTATION_VERSION")
    
    if not otel_dotnet_core_version:
        print("Error: OTEL_DOTNET_CORE_VERSION environment variable required")
        sys.exit(1)
    
    if not otel_dotnet_instrumentation_version:
        print("Error: OTEL_DOTNET_INSTRUMENTATION_VERSION environment variable required")
        sys.exit(1)
    
    csproj_path = 'src/AWS.Distro.OpenTelemetry.AutoInstrumentation/AWS.Distro.OpenTelemetry.AutoInstrumentation.csproj'
    csproj_dir = 'src/AWS.Distro.OpenTelemetry.AutoInstrumentation'
    build_cs_path = 'build/Build.cs'
    
    core_updated = update_core_packages(csproj_path, otel_dotnet_core_version)
    contrib_updated = update_contrib_packages(csproj_dir)
    build_cs_updated = update_build_cs_file(build_cs_path, otel_dotnet_instrumentation_version)
    
    if core_updated or contrib_updated or build_cs_updated:
        print(f"Dependencies updated to Core {otel_dotnet_core_version}")
    else:
        print("No updates were made")

if __name__ == '__main__':
    main()