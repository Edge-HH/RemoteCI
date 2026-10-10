namespace RemoteCI.Server.Pages;

/// <summary>_ClassAvatar 分部视图的输入：未上传头像时渲染班级图标占位。Version 用于头像更新后刷新浏览器缓存。</summary>
public sealed record ClassAvatarModel(Guid ClassId, bool HasAvatar, string CssClass = "class-avatar", string Alt = "", long? Version = null);
