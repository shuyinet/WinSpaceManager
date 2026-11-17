# WinSpaceManager

Windows 磁盘空间与应用管理工具，基于 C#、.NET 8 和 WPF。

## 功能

- 垃圾扫描与清理、C 盘空间分析
- 软件扫描、分类、排序与卸载
- 软件与大文件迁移、目录联接
- 启动项管理、残留扫描、文件粉碎
- 磁盘可视化、截图、操作历史与回滚

## 构建

需要 Windows、.NET 8 SDK，以及 NuGet 网络访问。

```powershell
dotnet restore sysmgr.sln
dotnet build sysmgr.sln -c Release
dotnet test sysmgr.sln -c Release
```

入口项目：`src/SysMgr.App`。程序需要管理员权限。

## 项目状态

项目正在开发中。软件迁移仍有权限兼容问题，部分设置与功能尚未完成；完整构建及测试尚待验证。涉及清理、卸载、迁移和粉碎时，请先备份重要数据。外部卸载程序的操作和文件粉碎无法保证恢复。

需求与设计说明见 `docs/`。内部项目名暂保留 SysMgr。
