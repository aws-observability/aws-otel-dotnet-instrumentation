#!/usr/bin/env python3

import requests
import re
import sys
import xml.etree.ElementTree as ET

def get_latest_versions_from_github():
    """Get the latest versions from GitHub releases."""
    try:
        print("Fetching releases from opentelemetry-dotnet...")
        dotnet_response = requests.get(
            'https://api.github.com/repos/open-telemetry/opentelemetry-dotnet/releases?per_page=50',
            timeout=30
        )
        dotnet_response.raise_for_status()
        
        print("Fetching releases from opentelemetry-dotnet-contrib...")
        contrib_response = requests.get(
            'https://api.github.com/repos/open-telemetry/opentelemetry-dotnet-contrib/releases?per_page=100',
            timeout=30
        )
        contrib_response.raise_for_status()
        
        dotnet_releases = dotnet_response.json()
        contrib_releases = contrib_response.json()
        
        print(f"Found {len(dotnet_releases)} dotnet releases")
        print(f"Found {len(contrib_releases)} contrib releases")
        
        versions = {}
        
        # Process opentelemetry-dotnet releases (core packages)
        for release in dotnet_releases:
            if release.get('prerelease', False):
                continue  # Skip pre-releases
            
            tag_name = release['tag_name']
            # Core releases are typically tagged as "v1.9.0" or "core-1.9.0"
            version_match = re.match(r'^(?:v|core-)?(\d+\.\d+\.\d+)$', tag_name)
            if version_match and 'core' not in versions:
                versions['core'] = version_match.group(1)
                print(f"Found core version: {versions['core']}")
                break  # Take the first (latest) stable release
        
        # Process opentelemetry-dotnet-contrib releases
        # Map package names to their release tag prefixes
        package_mappings = {
            'OpenTelemetry.Extensions.AWS': 'Extensions.AWS',
            'OpenTelemetry.Resources.AWS': 'Resources.AWS', 
            'OpenTelemetry.Instrumentation.AspNetCore': 'Instrumentation.AspNetCore',
            'OpenTelemetry.Instrumentation.AspNet': 'Instrumentation.AspNet',
            'OpenTelemetry.Instrumentation.AWSLambda': 'Instrumentation.AWS',  # Maps to AWS releases
            'OpenTelemetry.Instrumentation.Http': 'Instrumentation.Http',
            'OpenTelemetry.Sampler.AWS': 'Sampler.AWS',
            'OpenTelemetry.SemanticConventions': 'SemanticConventions'
        }
        
        for release in contrib_releases:
            if release.get('prerelease', False):
                continue  # Skip pre-releases
            
            tag_name = release['tag_name']
            # Parse contrib releases like "Instrumentation.AspNetCore-1.12.0"
            match = re.match(r'^(.+)-(\d+\.\d+\.\d+)$', tag_name)
            if match:
                component_name = match.group(1)
                version = match.group(2)
                
                # Find packages that map to this component
                for package_name, expected_component in package_mappings.items():
                    if component_name == expected_component and package_name not in versions:
                        versions[package_name] = version
                        print(f"Found {package_name}: {version}")
        
        return versions
        
    except requests.RequestException as request_error:
        print(f"Warning: Could not get GitHub releases: {request_error}")
        return {}

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

def update_csproj_file(file_path, github_versions):
    """Update OpenTelemetry package versions in a .csproj file."""
    try:
        # Parse the XML file
        tree = ET.parse(file_path)
        root = tree.getroot()
        
        updated = False
        
        # Package categorization
        core_packages = [
            'OpenTelemetry',
            'OpenTelemetry.Api', 
            'OpenTelemetry.Exporter.OpenTelemetryProtocol',
            'OpenTelemetry.Extensions.Propagators'
        ]
        
        # Find all PackageReference elements
        for package_ref in root.findall('.//PackageReference'):
            include = package_ref.get('Include', '')
            
            # Only update OpenTelemetry packages
            if include.startswith('OpenTelemetry'):
                current_version = package_ref.get('Version', '')
                latest_version = None
                
                # Try to get version from GitHub releases only
                if include in core_packages and 'core' in github_versions:
                    latest_version = github_versions['core']
                elif include in github_versions:
                    latest_version = github_versions[include]
                else:
                    # Skip packages not found in GitHub (likely only have pre-releases)
                    print(f"Skipping {include} - no stable release found in GitHub")
                    continue
                
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
    print("Getting latest versions from GitHub releases...")
    github_versions = get_latest_versions_from_github()
    
    if not github_versions:
        print("No versions found from GitHub releases, exiting")
        return
    
    print("Found versions:", github_versions)
    
    csproj_path = 'src/AWS.Distro.OpenTelemetry.AutoInstrumentation/AWS.Distro.OpenTelemetry.AutoInstrumentation.csproj'
    build_cs_path = 'build/Build.cs'
    
    csproj_updated = update_csproj_file(csproj_path, github_versions)
    build_cs_updated = update_build_cs_file(build_cs_path)
    
    if not csproj_updated and not build_cs_updated:
        print("No updates were made")

if __name__ == '__main__':
    main()
