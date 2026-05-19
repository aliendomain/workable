using System.Collections;

namespace Workable;

public abstract record WorkQueryListResult<TItem>(IReadOnlyList<TItem> Items) :
    IReadOnlyList<TItem>,
    IWorkQueryResult
{
    public int Count => this.Items.Count;

    public TItem this[int index] => this.Items[index];

    public IEnumerator<TItem> GetEnumerator()
        => this.Items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator()
        => this.GetEnumerator();
}
