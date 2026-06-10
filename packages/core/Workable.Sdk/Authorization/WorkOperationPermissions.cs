namespace Workable;

/// <summary>
/// Identifies the fine-grained work operations that a grant can allow.
/// </summary>
[Flags]
public enum WorkOperationPermissions
{
    /// <summary>
    /// No operations are allowed.
    /// </summary>
    None = 0,

    /// <summary>
    /// Allows queueing new workers for the definition.
    /// </summary>
    Queue = 1 << 0,

    /// <summary>
    /// Allows starting an existing worker.
    /// </summary>
    Start = 1 << 1,

    /// <summary>
    /// Allows pausing an existing worker.
    /// </summary>
    Pause = 1 << 2,

    /// <summary>
    /// Allows canceling an existing worker.
    /// </summary>
    Cancel = 1 << 3,

    /// <summary>
    /// Allows pushing an existing waiting worker.
    /// </summary>
    Push = 1 << 4,

    /// <summary>
    /// Allows purging an existing final worker.
    /// </summary>
    Purge = 1 << 5,

    /// <summary>
    /// Allows reconfiguring an existing worker.
    /// </summary>
    ReconfigureWorker = 1 << 6,

    /// <summary>
    /// Allows reconfiguring the definition itself for future workers.
    /// </summary>
    ReconfigureDefinition = 1 << 7,

    /// <summary>
    /// Allows all worker actions except reconfiguration.
    /// </summary>
    WorkerActions = Start | Pause | Cancel | Push | Purge,

    /// <summary>
    /// Allows worker and definition reconfiguration.
    /// </summary>
    Reconfigure = ReconfigureWorker | ReconfigureDefinition,

    /// <summary>
    /// Allows queueing, worker actions, and both reconfiguration surfaces.
    /// </summary>
    Operate = Queue | WorkerActions | Reconfigure,
}
