using System;
using Amazon.Runtime;

namespace TestSimpleApp.AWSSDK.Framework
{
    public class AmazonClientConfigHelper
    {
        private const string faultEndpoint = "http://fault.test:8080";
        private const string errorEndpoint = "http://error.test:8080";
        private static readonly TimeSpan defaultTimeout = TimeSpan.FromMilliseconds(100);

        public static T CreateConfig<T>(bool isFault = false) where T : ClientConfig, new()
        {
            return new T
            {
                ServiceURL = isFault ? faultEndpoint : errorEndpoint, Timeout = defaultTimeout, RetryMode = RequestRetryMode.Legacy
            };
        }
    }
}