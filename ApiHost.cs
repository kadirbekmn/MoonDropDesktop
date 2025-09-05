// using Microsoft.AspNetCore.Builder;
// using Microsoft.AspNetCore.Hosting;
// using Microsoft.Extensions.DependencyInjection;
// using Microsoft.Extensions.Hosting;
// using System.Threading.Tasks;
// using Microsoft.AspNetCore.Http;

// namespace MoonDropDesktop
// {
//     public class ApiHost
//     {
//         private IHost _host;

//         public async Task StartAsync()
//         {
//             _host = Host.CreateDefaultBuilder()
//                 .ConfigureWebHostDefaults(webBuilder =>
//                 {
//                     webBuilder.UseKestrel()
//                               .UseUrls("http://localhost:8080")
//                               .Configure(app =>
//                               {
//                                   var env = app.ApplicationServices.GetRequiredService<IHostEnvironment>();

//                                   if (env.IsDevelopment())
//                                   {
//                                       app.UseDeveloperExceptionPage();
//                                   }

//                                   app.UseRouting();
//                                   app.UseEndpoints(endpoints =>
//                                   {
//                                       endpoints.MapGet("/actuator/health", async context =>
//                                       {
//                                           context.Response.ContentType = "application/json";
//                                           await context.Response.WriteAsync("{\"status\":\"UP\"}");
//                                       });
//                                   });
//                               });
//                 })
//                 .Build();

//             await _host.StartAsync();
//         }

//         public async Task StopAsync()
//         {
//             if (_host != null)
//                 await _host.StopAsync();
//         }
//     }
// }
