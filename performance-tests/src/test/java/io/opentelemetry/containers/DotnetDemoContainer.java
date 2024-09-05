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

public class DotnetDemoContainer {
    

  private static final Logger logger =
      LoggerFactory.getLogger(DotnetDemoContainer.class);
  private static final int PORT = 8001;

  private final Network network;
  private final Startable collector;
  private final DistroConfig distroConfig;
  private final NamingConventions namingConventions;

  public DotnetDemoContainer(
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
            .withNetworkAliases("dotnet-demo")
            .withLogConsumer(new Slf4jLogConsumer(logger))
            .withExposedPorts(PORT)
            .waitingFor(Wait.forHttp("/outgoing-http-call").forPort(PORT))
            .withFileSystemBind(
                namingConventions.localResults(), namingConventions.containerResults())
            .withCopyFileToContainer(
                MountableFile.forClasspathResource("runDotnetDemo.sh"),
                "/app/run.sh")
            .withCopyFileToContainer(
                MountableFile.forClasspathResource("profiler.py"),
                "/app/profiler.py")
            .withCopyFileToContainer(
                MountableFile.forClasspathResource("executeProfiler.sh"),
                "/app/executeProfiler.sh")
            .withEnv("ASPNETCORE_URLS", "http://+:8001")
            .withEnv("LISTEN_ADDRESS", "0.0.0.0:8001")
            .withEnv("PORT", Integer.toString(PORT))
            .withEnv(distroConfig.getAdditionalEnvVars())
            .dependsOn(collector)
            .withCreateContainerCmdModifier(
                cmd -> cmd.getHostConfig().withCpusetCpus(RuntimeUtil.getApplicationCores()));
            // .withCommand("bash /app/run.sh");

    if (distroConfig.doInstrument()) {
      container
          .withEnv("DO_INSTRUMENT", "true")
          .withEnv("OTEL_TRACES_EXPORTER", "otlp")
          .withEnv("OTEL_METRICS_EXPORTER", "none")
          .withEnv("OTEL_IMR_EXPORT_INTERVAL", "5000")
          .withEnv("OTEL_EXPORTER_OTLP_INSECURE", "true")
          .withEnv("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4317")
          .withEnv("OTEL_RESOURCE_ATTRIBUTES", "service.name=dotnet-demo");
    }
    return container;
  }
}


