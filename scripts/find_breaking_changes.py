#!/usr/bin/env python3

import os
import re
import sys
import requests
from packaging import version

def get_current_version_from_csproj():
    """Extract current OpenTelemetry versions from .csproj file."""
    try:
        with open("src/AWS.Distro.OpenTelemetry.AutoInstrumentation/AWS.Distro.OpenTelemetry.AutoInstrumentation.csproj", "r", encoding="utf-8") as file:
            content = file.read()

        # Find OpenTelemetry core version
        core_match = re.search(r'<PackageReference\s+Include="OpenTelemetry"\s+Version="([^"]+)"', content)
        current_core_version = core_match.group(1) if core_match else None

        return current_core_version

    except (OSError, IOError) as error:
        print(f"Error reading current versions: {error}")
        return None

def get_releases_with_breaking_changes(repo, current_version, new_version):
    """Get releases between current and new version that mention breaking changes."""
    try:
        response = requests.get(f"https://api.github.com/repos/open-telemetry/{repo}/releases", timeout=30)
        response.raise_for_status()

        releases = response.json()
        breaking_releases = []

        for release in releases:
            # Skip pre-releases
            if release.get('prerelease', False):
                continue
                
            tag_name = release["tag_name"]
            
            # Extract version from tag (handle core-X.X.X format)
            if repo == "opentelemetry-dotnet":
                version_match = re.match(r'^(?:v|core-)?(\d+\.\d+\.\d+)$', tag_name)
                release_version = version_match.group(1) if version_match else None
            else:
                # For contrib, extract from component-version format
                version_match = re.match(r'^.+-(\d+\.\d+\.\d+)$', tag_name)
                release_version = version_match.group(1) if version_match else None

            if not release_version:
                continue

            # Check if this release is between current and new version
            try:
                if version.parse(release_version) > version.parse(current_version) and version.parse(release_version) <= version.parse(new_version):
                    # Check for breaking changes pattern
                    body = release.get("body", "")
                    if re.search(r'\*\s*\*\*Breaking\s+Change\*\*', body):
                        breaking_releases.append({
                            "version": release_version,
                            "name": release["name"],
                            "url": release["html_url"],
                            "tag": tag_name
                        })
            except (ValueError, KeyError) as parse_error:
                continue

        return breaking_releases

    except requests.RequestException as request_error:
        print(f"Error: Could not get releases for {repo}: {request_error}")
        return []

def main():
    new_core_version = os.environ.get("OTEL_DOTNET_CORE_VERSION")

    if not new_core_version:
        print("Error: OTEL_DOTNET_CORE_VERSION environment variable required")
        sys.exit(1)

    current_core_version = get_current_version_from_csproj()

    if not current_core_version:
        print("Could not determine current core version")
        sys.exit(1)

    print("Checking for breaking changes:")
    print(f"Core: {current_core_version} → {new_core_version}")

    # Check core repo for breaking changes
    core_breaking = get_releases_with_breaking_changes("opentelemetry-dotnet", current_core_version, new_core_version)

    # Log findings
    if core_breaking:
        print("Found releases with breaking changes:")
        for release in core_breaking:
            print(f"  - {release['name']}: {release['url']}")
    else:
        print("No breaking changes detected in releases.")

    # Output for GitHub Actions
    breaking_info = ""

    if core_breaking:
        breaking_info += "**opentelemetry-dotnet:**\\n"
        for release in core_breaking:
            breaking_info += f"- [{release['name']}]({release['url']})\\n"

    if not breaking_info:
        breaking_info = "No breaking changes detected in releases."

    # Set GitHub output
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output_file:
            output_file.write(f"breaking_changes_info<<EOF\\n{breaking_info}EOF\\n")

if __name__ == "__main__":
    main()