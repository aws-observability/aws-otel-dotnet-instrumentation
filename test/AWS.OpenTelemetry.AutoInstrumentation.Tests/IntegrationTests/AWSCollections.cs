// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace AWS.OpenTelemetry.AutoInstrumentation.Tests.IntegrationTests;

[CollectionDefinition(Name)]
public class AWSCollection : ICollectionFixture<AWSFixture>
{
    public const string Name = nameof(AWSCollection);
}

public class AWSFixture : IAsyncLifetime
{
    private const int LocalStackPort = 4566;
    private static readonly string AWSLocalStackImage = "localstack/localstack";
    private IContainer? container;

    public AWSFixture()
    {
    }

    public async Task InitializeAsync()
    {
        this.container = await LaunchAWSLocalStackContainerAsync();
    }

    public async Task DisposeAsync()
    {
        if (this.container != null)
        {
            await ShutdownAWSLocalStackContainerAsync(this.container);
        }
    }

    private static async Task<IContainer> LaunchAWSLocalStackContainerAsync()
    {
        var containerName = string.Format("aws-localstack{0}", LocalStackPort);
        var containersBuilder = new ContainerBuilder()
            .WithImage(AWSLocalStackImage)
            .WithName(containerName)
            .WithPortBinding(LocalStackPort, LocalStackPort)
            .WithPortBinding(4510, 4559)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(LocalStackPort));

        var container = containersBuilder.Build();
        await container.StartAsync();

        return container;
    }

    private static async Task ShutdownAWSLocalStackContainerAsync(IContainer container)
    {
        await container.DisposeAsync();
    }
}
