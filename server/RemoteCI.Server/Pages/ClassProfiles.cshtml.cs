using Microsoft.AspNetCore.Identity;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;

namespace RemoteCI.Server.Pages;

/// <summary>与档案库共用服务和编辑器，所有读写与下发目标都锁定当前班级。</summary>
public sealed class ClassProfilesModel(UserManager<AppUser> users, ProfileLibraryService library,
    ProfileDispatchService dispatch, DeviceInventoryService devices, ClassroomService classrooms)
    : ProfilesModel(users, library, dispatch, devices, classrooms)
{
    public override bool IsClassPage => true;
}
