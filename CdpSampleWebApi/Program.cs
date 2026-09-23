// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace CdpSampleWebApi
{
    /// <summary>
    /// Entry point for the CdpSampleWebApi application.
    /// </summary>
    public partial class Program
    {
        /// <summary>
        /// Gets the UTC time when the application started.
        /// </summary>
        public static DateTime StartupTime { get; private set; }

        /// <summary>
        /// Main entry point for the application. Configures and runs the web host.
        /// </summary>
        /// <param name="args">Command-line arguments.</param>
        public static void Main(string[] args)
        {
            Program.StartupTime = DateTime.UtcNow;

            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddWindowsService(options =>
            {
                options.ServiceName = "National Grid GIS Enhanced Connector";
            });

            // Add services to the container.

            builder.Services.AddControllers(mvcOptions =>
            {
                mvcOptions.Filters.Add(new ExceptionHandler());
            });

            // The provider factory is the critical piece to 
            // determine which datasource we have. 
            
            //builder.Services.AddSingleton<ITableProviderFactory, DatabricksTableProviderFactory>();
            builder.Services.AddHttpClient("ArcGIS");
            builder.Services.AddHttpClient("ArcGISAuth");
            builder.Services.AddSingleton<ArcGisOAuthTokenProvider>();
            builder.Services.AddSingleton<ITableProviderFactory, ArcGisTableProviderFactory>();

            var app = builder.Build();

            // Configure the HTTP request pipeline.

            // HTTP-only local POC. TLS will be terminated by the approved production host.

            app.UseAuthorization();

            app.MapControllers();

            app.Run();
        }
    }
}
