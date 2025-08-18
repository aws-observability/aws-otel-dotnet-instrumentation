using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Contrib.Extensions.AWSXRay.Trace;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;
using System;
using System.Diagnostics;
using dotnet_sample_app.Controllers;
using AWS.Distro.OpenTelemetry.Exporter.Xray.Udp;


namespace dotnet_sample_app
{
    public class Startup
    {        
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddControllers();

            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

            if(!String.IsNullOrEmpty(Environment.GetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES"))) {
                var resourceBuilder = ResourceBuilder.CreateDefault().AddTelemetrySdk();
                Sdk.CreateTracerProviderBuilder()
                    .AddSource("dotnet-sample-app")
                    .SetResourceBuilder(resourceBuilder)
                    .AddAWSInstrumentation()
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddOtlpExporter(options => 
                    {
                        options.Endpoint = new Uri(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"));
                    })
                    .AddXrayUdpExporter(resourceBuilder.Build(), "localhost:2000")
                    .Build();
            }
            else {
                var resourceBuilder = ResourceBuilder.CreateDefault()
                .AddService(serviceName: "dotnet-sample-app")  // Added first
                .AddTelemetrySdk();                           
                Sdk.CreateTracerProviderBuilder()
                    .AddSource("dotnet-sample-app")
                    .SetResourceBuilder(resourceBuilder)
                    .AddAWSInstrumentation()
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddOtlpExporter(options => 
                    {
                        options.Endpoint = new Uri(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"));
                    })
                    .AddXrayUdpExporter(resourceBuilder.Build(), "localhost:2000")
                    .Build();
            }

            Sdk.CreateMeterProviderBuilder()
                .AddMeter("adot")
                .AddOtlpExporter()
                .Build();
                
            Sdk.SetDefaultTextMapPropagator(new AWSXRayPropagator());


        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseRouting();

            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
            });
        }
    }

    public static class TracerProviderBuilderExtensions
    {
        public static TracerProviderBuilder AddXrayUdpExporter(
            this TracerProviderBuilder builder, 
            Resource resource, 
            string endpoint)
        {
            return builder.AddProcessor(
                new BatchActivityExportProcessor(
                    new XrayUdpExporter(resource, endpoint)
                )
            );
        }
    }
}
