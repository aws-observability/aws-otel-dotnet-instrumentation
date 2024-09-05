package io.opentelemetry.containers;

import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.testcontainers.containers.GenericContainer;
import org.testcontainers.containers.Network;
import org.testcontainers.containers.PostgreSQLContainer;
import org.testcontainers.containers.output.Slf4jLogConsumer;
import org.testcontainers.containers.wait.strategy.Wait;
import org.testcontainers.lifecycle.Startable;
import org.testcontainers.utility.DockerImageName;
import org.testcontainers.utility.MountableFile;

import io.opentelemetry.distros.DistroConfig;
import io.opentelemetry.util.NamingConventions;
import io.opentelemetry.util.RuntimeUtil;

public class DotnetRemoteContainer {
    

  private static final Logger logger =
      LoggerFactory.getLogger(DotnetRemoteContainer.class);
  private static final int PORT = 8002;

  private final Network network;
  private final Startable collector;
  private final DistroConfig distroConfig;
  private final NamingConventions namingConventions;

  public DotnetRemoteContainer(
      Network network,
      Startable collector,
      DistroConfig distroConfig,
      NamingConventions namingConventions) {
    this.network = network;
    this.collector = collector;
    this.distroConfig = distroConfig;
    this.namingConventions = namingConventions;
  }

  public GenericContainer<?> build() {
    GenericContainer<?> container =
        new GenericContainer<>(DockerImageName.parse("performance-test/dotnet-demo"))
            .withNetwork(network)
            .withNetworkAliases("dotnet-remote")
            .withLogConsumer(new Slf4jLogConsumer(logger))
            .withExposedPorts(PORT)
            .waitingFor(Wait.forHttp("/outgoing-http-call").forPort(PORT))
            .withFileSystemBind(
                namingConventions.localResults(), namingConventions.containerResults())
            // .withCopyFileToContainer(
            //     MountableFile.forClasspathResource("runDotnetDemo.sh"),
            //     "/app/run.sh")
            // .withCopyFileToContainer(
            //     MountableFile.forClasspathResource("profiler.py"),
            //     "/app/profiler.py")
            // .withCopyFileToContainer(
            //     MountableFile.forClasspathResource("executeProfiler.sh"),
                // "/app/executeProfiler.sh")
            .withEnv("ASPNETCORE_URLS", "http://+:8002")
            .withEnv("LISTEN_ADDRESS", "0.0.0.0:8002")
            .withEnv("PORT", Integer.toString(PORT))
            .withEnv(distroConfig.getAdditionalEnvVars())
            .dependsOn(collector)
            .withCreateContainerCmdModifier(
                cmd -> cmd.getHostConfig().withCpusetCpus(RuntimeUtil.getApplicationCores()));
    return container;
  }
}



