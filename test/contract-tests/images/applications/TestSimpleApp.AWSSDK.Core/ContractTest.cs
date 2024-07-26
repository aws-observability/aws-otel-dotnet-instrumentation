namespace TestSimpleApp.AWSSDK.Core;

public abstract class ContractTest(ILogger logger)
{
    protected abstract Task CreateFault(CancellationToken cancellationToken);
    protected abstract Task CreateError(CancellationToken cancellationToken);

    public async Task<IResult> Fault()
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
            logger.LogError("Expected exception occurred {exception}", exception);
        }

        return Results.StatusCode(500);
    }

    public async Task<IResult> Error()
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
            logger.LogError("Expected exception occurred {exception}", exception);
        }

        return Results.BadRequest();
    }
}