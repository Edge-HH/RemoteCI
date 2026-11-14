using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 考试绑定与待放映任务。票据和考试端令牌只存摘要；明文只在签发当次返回。
/// 数据写在数据库旁的 exam-registry.json，不进入备份里的业务表。
/// </summary>
public sealed class ExamRegistry
{
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan AgentOnlineWindow = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan JobLifetime = TimeSpan.FromHours(6);

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly string _path;
    private Store _store;

    public ExamRegistry(IOptions<ServerOptions> options, IHostEnvironment environment, TimeProvider time)
        : this(ResolvePath(options.Value, environment), time)
    {
    }

    public ExamRegistry(string path, TimeProvider time)
    {
        _path = path;
        _time = time;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _store = Load(path);
    }

    public (string Ticket, DateTimeOffset ExpiresAt) IssueTicket(Guid classId, string className)
    {
        var ticket = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var expires = _time.GetUtcNow().Add(TicketLifetime);
        lock (_gate)
        {
            _store.Tickets.RemoveAll(x => x.ClassId == classId);
            _store.Tickets.Add(new TicketRecord
            {
                Hash = Hash(ticket),
                ClassId = classId,
                ClassName = className,
                ExpiresAt = expires,
            });
            Save();
        }
        return (ticket, expires);
    }

    public ExamBindResponse? Bind(string? ticket, (Guid Id, string Name)? pairClass, string? deviceName)
    {
        (Guid Id, string Name)? matched = null;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            _store.Tickets.RemoveAll(x => x.ExpiresAt <= now);
            if (!string.IsNullOrWhiteSpace(ticket))
            {
                var hash = Hash(ticket.Trim());
                var found = _store.Tickets.FirstOrDefault(x => FixedEquals(x.Hash, hash));
                if (found is not null) matched = (found.ClassId, found.ClassName);
            }
            matched ??= pairClass;
            if (matched is not { } classroom) return null;

            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            _store.Agents.RemoveAll(x => x.ClassId == classroom.Id);
            _store.Agents.Add(new AgentRecord
            {
                Hash = Hash(token),
                ClassId = classroom.Id,
                ClassName = classroom.Name,
                DeviceName = Clip(deviceName, 80) ?? "ExamAware",
                BoundAt = now,
                LastSeenAt = now,
            });
            Save();
            return new ExamBindResponse
            {
                Token = token,
                ClassId = classroom.Id,
                ClassName = classroom.Name,
            };
        }
    }

    public AgentView? Authenticate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        lock (_gate)
        {
            var hash = Hash(token.Trim());
            var agent = _store.Agents.FirstOrDefault(x => FixedEquals(x.Hash, hash));
            if (agent is null) return null;
            agent.LastSeenAt = _time.GetUtcNow();
            Save();
            return ToView(agent);
        }
    }

    public IReadOnlyList<AgentView> ListAgents()
    {
        lock (_gate) return _store.Agents.Select(ToView).ToList();
    }

    public ExamJobView CreateJob(Guid classId, string className, string fileName, string sha256)
    {
        var id = Guid.NewGuid();
        var job = new JobRecord
        {
            Id = id,
            ClassId = classId,
            ClassName = className,
            FileName = fileName,
            Sha256 = sha256,
            LocalFileName = ExamDistributeRequest.FileNameFor(id),
            State = "pending",
            CreatedAt = _time.GetUtcNow(),
        };
        lock (_gate)
        {
            _store.Jobs.Add(job);
            if (_store.Jobs.Count > 200)
                _store.Jobs = _store.Jobs.OrderByDescending(x => x.CreatedAt).Take(200).ToList();
            Save();
        }
        return ToJob(job);
    }

    public void FailJob(Guid jobId, string message)
    {
        lock (_gate)
        {
            var job = _store.Jobs.FirstOrDefault(x => x.Id == jobId);
            if (job is null || job.State is "started" or "failed") return;
            job.State = "failed";
            job.Message = Clip(message, 300);
            Save();
        }
    }

    public IReadOnlyList<ExamJobView> OpenJobs(Guid classId)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var changed = false;
            foreach (var job in _store.Jobs.Where(x => x.State == "pending" && now - x.CreatedAt > JobLifetime))
            {
                job.State = "failed";
                job.Message = "超过 6 小时仍未放映";
                changed = true;
            }
            if (changed) Save();
            return _store.Jobs
                .Where(x => x.ClassId == classId && x.State == "pending")
                .OrderBy(x => x.CreatedAt)
                .Select(ToJob)
                .ToList();
        }
    }

    public bool Ack(Guid classId, Guid jobId, string? state, string? message)
    {
        if (state is not ("started" or "failed")) return false;
        lock (_gate)
        {
            var job = _store.Jobs.FirstOrDefault(x => x.Id == jobId && x.ClassId == classId);
            if (job is null || job.State != "pending") return job?.State == state;
            job.State = state;
            job.Message = Clip(message, 300);
            Save();
            return true;
        }
    }

    public IReadOnlyList<JobListItem> RecentJobs()
    {
        lock (_gate)
        {
            return _store.Jobs.OrderByDescending(x => x.CreatedAt).Take(30).Select(x => new JobListItem(
                x.Id, x.ClassId, x.ClassName, x.FileName, x.State, x.Message, x.CreatedAt)).ToList();
        }
    }

    public void Stage(string kind, string transferId, string fileName, long size, string? sourceKey = null)
    {
        var asset = new StagedAsset
        {
            TransferId = transferId,
            FileName = fileName,
            Size = size,
            SourceKey = sourceKey,
            StagedAt = _time.GetUtcNow(),
        };
        lock (_gate)
        {
            switch (kind)
            {
                case "installer": _store.Installer = asset; break;
                case "plugin": _store.Plugin = asset; break;
                case "exam": _store.Exam = asset; break;
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
            Save();
        }
    }

    public StagedAsset? Staged(string kind)
    {
        lock (_gate)
        {
            return kind switch
            {
                "installer" => _store.Installer,
                "plugin" => _store.Plugin,
                "exam" => _store.Exam,
                _ => null,
            };
        }
    }

    private static AgentView ToView(AgentRecord agent) =>
        new(agent.ClassId, agent.ClassName, agent.DeviceName, agent.LastSeenAt);

    private static ExamJobView ToJob(JobRecord job) => new()
    {
        Id = job.Id,
        FileName = job.FileName,
        Sha256 = job.Sha256,
        LocalFileName = job.LocalFileName,
        State = job.State,
    };

    private void Save()
    {
        var json = JsonSerializer.Serialize(_store, JsonDefaults.Options);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, _path, true);
    }

    private static Store Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new Store();
            return JsonSerializer.Deserialize<Store>(File.ReadAllText(path), JsonDefaults.Options) ?? new Store();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new Store();
        }
    }

    private static string ResolvePath(ServerOptions options, IHostEnvironment environment)
    {
        var databasePath = Path.IsPathRooted(options.DatabasePath)
            ? options.DatabasePath
            : Path.Combine(environment.ContentRootPath, options.DatabasePath);
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, "exam-registry.json");
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool FixedEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string? Clip(string? value, int max)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        return text.Length <= max ? text : text[..max];
    }

    private sealed class Store
    {
        public List<TicketRecord> Tickets { get; set; } = [];
        public List<AgentRecord> Agents { get; set; } = [];
        public List<JobRecord> Jobs { get; set; } = [];
        public StagedAsset? Installer { get; set; }
        public StagedAsset? Plugin { get; set; }
        public StagedAsset? Exam { get; set; }
    }

    private sealed class TicketRecord
    {
        public string Hash { get; set; } = string.Empty;
        public Guid ClassId { get; set; }
        public string ClassName { get; set; } = string.Empty;
        public DateTimeOffset ExpiresAt { get; set; }
    }

    private sealed class AgentRecord
    {
        public string Hash { get; set; } = string.Empty;
        public Guid ClassId { get; set; }
        public string ClassName { get; set; } = string.Empty;
        public string DeviceName { get; set; } = "ExamAware";
        public DateTimeOffset BoundAt { get; set; }
        public DateTimeOffset LastSeenAt { get; set; }
    }

    private sealed class JobRecord
    {
        public Guid Id { get; set; }
        public Guid ClassId { get; set; }
        public string ClassName { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public string LocalFileName { get; set; } = string.Empty;
        public string State { get; set; } = "pending";
        public string? Message { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }

    public sealed class StagedAsset
    {
        public string TransferId { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long Size { get; set; }
        /// <summary>官方 Release 的“仓库@标签/文件名”；手动上传时为空。</summary>
        public string? SourceKey { get; set; }
        public DateTimeOffset StagedAt { get; set; }
    }
}

public sealed record AgentView(Guid ClassId, string ClassName, string DeviceName, DateTimeOffset LastSeenAt);

public sealed record JobListItem(
    Guid Id, Guid ClassId, string ClassName, string FileName, string State, string? Message, DateTimeOffset CreatedAt);
