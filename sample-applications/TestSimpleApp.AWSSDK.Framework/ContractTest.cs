using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using Microsoft.Owin.Logging;

namespace TestSimpleApp.AWSSDK.Framework
{
    public abstract class ContractTestController : ApiController
    {
        protected abstract Task CreateFault(CancellationToken cancellationToken);
        protected abstract Task CreateError(CancellationToken cancellationToken);

        public virtual async Task<IHttpActionResult> Fault()
        {
            try
            {
                var cancellationTokenSource = new CancellationTokenSource();
                cancellationTokenSource.CancelAfter(TimeSpan.FromMilliseconds(2000));

                var task = CreateFault(cancellationTokenSource.Token);
                cancellationTokenSource.Token.ThrowIfCancellationRequested();

                await task;
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Expected exception occurred {exception}");
            }

            return StatusCode(HttpStatusCode.InternalServerError);
        }

        public virtual async Task<IHttpActionResult> Error()
        {
            try
            {
                var cancellationTokenSource = new CancellationTokenSource();
                cancellationTokenSource.CancelAfter(TimeSpan.FromMilliseconds(2000));

                var task = CreateError(cancellationTokenSource.Token);
                cancellationTokenSource.Token.ThrowIfCancellationRequested();

                await task;
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Expected exception occurred {exception}");
            }

            return BadRequest();
        }
    }
}