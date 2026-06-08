namespace Workable.SampleHost.Demo;

public sealed class DemoSampleSystemSelection
{
    private readonly Lock sync = new();
    private bool operationsEnabled = true;
    private bool fulfillmentEnabled = true;

    public DemoWorkloadSystems Current
    {
        get
        {
            lock (this.sync)
            {
                return new DemoWorkloadSystems(this.operationsEnabled, this.fulfillmentEnabled);
            }
        }
    }

    public DemoWorkloadSystems Set(DemoWorkloadSystemsRequest request)
    {
        lock (this.sync)
        {
            this.operationsEnabled = request.Operations;
            this.fulfillmentEnabled = request.Fulfillment;
            return new DemoWorkloadSystems(this.operationsEnabled, this.fulfillmentEnabled);
        }
    }
}
