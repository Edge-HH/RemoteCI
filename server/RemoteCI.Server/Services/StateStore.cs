using System.Collections.Concurrent;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

public sealed class StateStore(LessonOverrideTable? overrides = null) : IStateStore
{
    private readonly ConcurrentDictionary<Guid, ClassStateBucket> _buckets = new();
    private readonly LessonOverrideTable _overrides = overrides ?? new LessonOverrideTable();

    private ClassStateBucket Bucket(Guid classId) => _buckets.GetOrAdd(classId, _ => new ClassStateBucket());

    public void SaveSnapshot(Guid classId, ClassStateSnapshot snapshot) => Bucket(classId).SaveSnapshot(snapshot);

    public ClassStateSnapshot? GetLatestSnapshot(Guid classId) => Bucket(classId).GetLatestSnapshot();

    public void SaveSchedule(Guid classId, ScheduleBundle schedule) => Bucket(classId).SaveSchedule(schedule);

    public ScheduleBundle? GetLatestSchedule(Guid classId) =>
        Bucket(classId).GetLatestSchedule() is { } bundle ? _overrides.Apply(classId, bundle) : null;

    public ScheduleBundle? GetSourceSchedule(Guid classId) => Bucket(classId).GetLatestSchedule();

    public void SaveEvent(Guid classId, ClassEvent @event) => Bucket(classId).SaveEvent(@event);

    public ClassEvent? GetLatestEvent(Guid classId) => Bucket(classId).GetLatestEvent();

    public void SaveExtensions(Guid classId, IReadOnlyList<ExtensionDefinition> extensions) =>
        Bucket(classId).SaveExtensions(extensions);

    public IReadOnlyList<ExtensionDefinition>? GetLatestExtensions(Guid classId) =>
        Bucket(classId).GetLatestExtensions();

    public void SaveExtensionGroups(Guid classId, IReadOnlyList<ExtensionGroupDefinition> groups) =>
        Bucket(classId).SaveExtensionGroups(groups);

    public IReadOnlyList<ExtensionGroupDefinition>? GetLatestExtensionGroups(Guid classId) =>
        Bucket(classId).GetLatestExtensionGroups();

    /// <summary>单个班级的不可变快照桶：读写在各自字段上整体替换，Volatile 保证跨线程可见性，无需锁。</summary>
    private sealed class ClassStateBucket
    {
        private ClassStateSnapshot? _snapshot;
        private ScheduleBundle? _schedule;
        private ClassEvent? _event;
        private IReadOnlyList<ExtensionDefinition>? _extensions;
        private IReadOnlyList<ExtensionGroupDefinition>? _extensionGroups;

        public void SaveSnapshot(ClassStateSnapshot snapshot) => Volatile.Write(ref _snapshot, snapshot);
        public ClassStateSnapshot? GetLatestSnapshot() => Volatile.Read(ref _snapshot);

        public void SaveSchedule(ScheduleBundle schedule) => Volatile.Write(ref _schedule, schedule);
        public ScheduleBundle? GetLatestSchedule() => Volatile.Read(ref _schedule);

        public void SaveEvent(ClassEvent @event) => Volatile.Write(ref _event, @event);
        public ClassEvent? GetLatestEvent() => Volatile.Read(ref _event);

        public void SaveExtensions(IReadOnlyList<ExtensionDefinition> extensions) =>
            Volatile.Write(ref _extensions, extensions);
        public IReadOnlyList<ExtensionDefinition>? GetLatestExtensions() => Volatile.Read(ref _extensions);

        public void SaveExtensionGroups(IReadOnlyList<ExtensionGroupDefinition> groups) =>
            Volatile.Write(ref _extensionGroups, groups);
        public IReadOnlyList<ExtensionGroupDefinition>? GetLatestExtensionGroups() => Volatile.Read(ref _extensionGroups);
    }
}
