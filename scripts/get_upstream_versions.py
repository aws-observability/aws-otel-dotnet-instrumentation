#!/usr/bin/env python3

import os
import re
import requests
import sys

def get_latest_core_version():
    """Get the latest core version from GitHub releases."""
    try:
        response = requests.get(
            'https://api.github.com/repos/open-telemetry/opentelemetry-dotnet/releases?per_page=50',
            timeout=30
        )
        response.raise_for_status()
        
        releases = response.json()
        
        for release in releases:
            if release.get('prerelease', False):
                continue
            
            tag_name = release['tag_name']
            version_match = re.match(r'^(?:v|core-)?(\d+\.\d+\.\d+)$', tag_name)
            if version_match:
                return version_match.group(1)
        return None
        
    except requests.RequestException as request_error:
        print(f"Error getting OpenTelemetry core version: {request_error}")
        sys.exit(1)

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
        print(f"Error getting dotnet-instrumentation version: {request_error}")
        sys.exit(1)

def main():
    otel_dotnet_core_version = get_latest_core_version()
    instrumentation_version = get_latest_dotnet_instrumentation_version()
    
    if not otel_dotnet_core_version:
        print("Error: Could not get core version - no stable releases found")
        sys.exit(1)
    
    print(f"OTEL_DOTNET_CORE_VERSION={otel_dotnet_core_version}")
    print(f"OTEL_DOTNET_INSTRUMENTATION_VERSION={instrumentation_version}")
    
    # Write to GitHub output if in CI
    if "GITHUB_OUTPUT" in os.environ:
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output_file:
            output_file.write(f"otel_dotnet_core_version={otel_dotnet_core_version}\\n")
            output_file.write(f"otel_dotnet_instrumentation_version={instrumentation_version}\\n")

if __name__ == "__main__":
    main()