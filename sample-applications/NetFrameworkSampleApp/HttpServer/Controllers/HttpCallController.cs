using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;

namespace HttpServer.Controllers
{
    public class HttpCallController : Controller
    {
        // GET: /outgoing-http-call
        public async Task<ActionResult> OutgoingHttpCall()
        {
            using (HttpClient client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(10);

                try
                {
                    HttpResponseMessage response = await client.GetAsync("https://aws.amazon.com");
                    response.EnsureSuccessStatusCode();

                    string responseBody = await response.Content.ReadAsStringAsync();

                    return Content(responseBody.Substring(0, 1000));
                }
                catch (Exception ex)
                {
                    return Content("Error: " + ex.Message);
                }
            }
        }
    }
}