#!/usr/bin/env python3

import requests
import re
import sys
import xml.etree.ElementTree as ET

def get_latest_version(package_name):
    """Get the latest version of a NuGet package."""
    try:
        response = requests.get(
            f'https://api.nuget.org/v3-flatcontainer/{package_name.lower()}/index.json',
            timeout=30
        )
        response.raise_for_status()
        
        data = response.json()
        versions = data.get('versions', [])
        
        if not versions:
            print(f"Warning: No versions found for {package_name}")
            return None
            
        # Get the latest stable version (avoid pre-release versions for now)
        stable_versions = [v for v in versions if not any(pre in v.lower() for pre in ['alpha', 'beta', 'rc', 'preview'])]
        
        if stable_versions:
            return stable_versions[-1]  # Last version is typically the latest
        else:
            # If no stable versions, use the latest version
            return versions[-1]
            
    except requests.RequestException as request_error:
        print(f"Warning: Could not get latest version for {package_name}: {request_error}")
        return None

def get_latest_dotnet_instrumentation_version():
    """Get the latest version of opentelemetry-dotnet-instrumentation from GitHub releases."""
    try:
        response = requests.get(
            'https://api.github.com/repos/open-telemetry/opentelemetry-dotnet-instrumentation/releases/latest',
            timeout=30
        )
        response.raise_for_status()
        
        release_data = response.json()
        tag_name = release_data['tag_name']
        
        return tag_name
        
    except requests.RequestException as request_error:
        print(f"Warning: Could not get latest dotnet-instrumentation version: {request_error}")
        return None

def update_csproj_file(file_path):
    """Update OpenTelemetry package versions in a .csproj file."""
    try:
        # Parse the XML file
        tree = ET.parse(file_path)
        root = tree.getroot()
        
        updated = False
        
        # Find all PackageReference elements
        for package_ref in root.findall('.//PackageReference'):
            include = package_ref.get('Include', '')
            
            # Only update OpenTelemetry packages
            if include.startswith('OpenTelemetry'):
                current_version = package_ref.get('Version', '')
                latest_version = get_latest_version(include)
                
                if latest_version and current_version != latest_version:
                    package_ref.set('Version', latest_version)
                    updated = True
                    print(f"Updated {include}: {current_version} → {latest_version}")
                elif latest_version:
                    print(f"{include} already at latest version: {latest_version}")
        
        if updated:
            tree.write(file_path, encoding='utf-8', xml_declaration=True)
            print("Dependencies updated successfully")
            return True
        else:
            print("No OpenTelemetry dependencies needed updating")
            return False
            
    except (ET.ParseError, OSError, IOError) as file_error:
        print(f"Error updating dependencies: {file_error}")
        sys.exit(1)

def update_build_cs_file(file_path):
    """Update the openTelemetryAutoInstrumentationDefaultVersion in Build.cs."""
    try:
        latest_version = get_latest_dotnet_instrumentation_version()
        if not latest_version:
            print("Could not get latest dotnet-instrumentation version")
            return False
        
        with open(file_path, 'r', encoding='utf-8') as input_file:
            content = input_file.read()
        
        pattern = r'private const string OpenTelemetryAutoInstrumentationDefaultVersion = "v[^"]*";'
        replacement = f'private const string OpenTelemetryAutoInstrumentationDefaultVersion = "{latest_version}";'
        
        if re.search(pattern, content):
            new_content = re.sub(pattern, replacement, content)
            
            if new_content != content:
                with open(file_path, 'w', encoding='utf-8') as output_file:
                    output_file.write(new_content)
                print(f"Updated OpenTelemetryAutoInstrumentationDefaultVersion to {latest_version}")
                return True
            else:
                print(f"OpenTelemetryAutoInstrumentationDefaultVersion already at latest version: {latest_version}")
                return False
        else:
            print("Could not find OpenTelemetryAutoInstrumentationDefaultVersion in Build.cs")
            return False
            
    except (OSError, IOError) as file_error:
        print(f"Error updating Build.cs: {file_error}")
        sys.exit(1)

def main():
    csproj_path = 'src/AWS.Distro.OpenTelemetry.AutoInstrumentation/AWS.Distro.OpenTelemetry.AutoInstrumentation.csproj'
    build_cs_path = 'build/Build.cs'
    
    csproj_updated = update_csproj_file(csproj_path)
    build_cs_updated = update_build_cs_file(build_cs_path)
    
    if not csproj_updated and not build_cs_updated:
        print("No updates were made")

if __name__ == '__main__':
    main()
