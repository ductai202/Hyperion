using Hyperion.Protocol;
using Xunit;

namespace Hyperion.Core.Tests;

public class WorkerTests
{
    [Fact]
    public async Task RunLoop_ShouldRunActiveExpiryPeriodically()
    {
        var worker = new Worker(0);

        var setTask = new WorkerTask(new RespCommand
        {
            Cmd = "SET",
            Args = ["worker:expired", "value", "EX", "1"]
        });
        await worker.EnqueueTaskAsync(setTask);
        await setTask.ReplyCompletion.Task;

        await Task.Delay(1100);

        for (int i = 0; i < 100; i++)
        {
            var pingTask = new WorkerTask(new RespCommand { Cmd = "PING", Args = [] });
            await worker.EnqueueTaskAsync(pingTask);
            await pingTask.ReplyCompletion.Task;
        }

        Assert.DoesNotContain(
            worker.Storage.DictStore.GetAllEntries(),
            entry => entry.Key == "worker:expired");
    }
}
