using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;

namespace TestSimpleApp.AWSSDK.Core;

public class SecretsManagerTests(
    IAmazonSecretsManager secretsManager,
    [FromKeyedServices("fault-secretsmanager")] IAmazonSecretsManager faultSecretsManager,
    [FromKeyedServices("error-secretsmanager")] IAmazonSecretsManager errorSecretsManager,
    ILogger<SecretsManagerTests> logger) : ContractTest(logger)
{
    public Task<CreateSecretResponse> CreateSecret()
    {
        return secretsManager.CreateSecretAsync(new CreateSecretRequest {
            Name = "test-secret", SecretString = "{\"key\":\"test\",\"value\":\"test\"}"
        });
    }

    public Task<GetSecretValueResponse> GetSecretValue()
    {
        return secretsManager.GetSecretValueAsync(new GetSecretValueRequest { SecretId = "test-secret" });
    }

    protected override Task CreateFault(CancellationToken cancellationToken)
    {
        return faultSecretsManager.CreateSecretAsync(new CreateSecretRequest { Name = "test-secret" }, cancellationToken);
    }

    protected override Task CreateError(CancellationToken cancellationToken)
    {
        return errorSecretsManager.DescribeSecretAsync(new DescribeSecretRequest { SecretId = "arn:aws:secretsmanager:us-east-1:000000000000:secret:test-secret-error" });
    }
}