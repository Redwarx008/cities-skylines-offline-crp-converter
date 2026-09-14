# OfflineCrpConverter

`OfflineCrpConverter` 是一个独立的 .NET 8 命令行工具（CLI），用于将
*Cities: Skylines* 的 `.crp` 资产转换成纹理内嵌的 glTF 2.0 Binary（`.glb`）。

它直接解析 CRP，不启动游戏、不加载游戏或 ModTools DLL、不调用 Blender。每次成功转换只生成一个
`.glb`；不会附带 OBJ、MTL、FBX、来源记录或校验报告。

## 当前能力

- 支持 CRP 格式版本 4–10；
- 解析 `BuildingInfo`、`PropInfo`、`VehicleInfo` 和 `NetInfo` 的主渲染几何；
- 按 checksum 连接 `MeshFilter`、`MeshRenderer` 材质槽和材质贴图；
- 递归导出建筑的 `m_subMeshes` 与 `m_subBuildings`；
- 解析网络资产的车道、路段和节点，并在空网络根节点中选择可渲染的代表模型；
- 保留材质槽顺序，跳过 Unity Mesh 中合法的空子网格；
- 将 Unity 左手坐标系转换为 glTF 右手坐标系，并同步修正三角形绕序；
- 保留源 UV，不执行旧 OBJ/Blender 工作流常见的额外 V 翻转；
- 将 DDS 漫反射贴图转换为内嵌 PNG；
- 将建筑和道具 Shader 使用的反相 `ACIMap.R` 合成到基础色 alpha，并使用 `MASK`；
- 将材质设为 `doubleSided`，避免单面旧资产因观察方向不同而消失；
- 在写盘前回读 GLB，验证容器、场景、网格、图元、材质、bufferView 与内嵌 PNG；
- 主模型没有可导出几何时明确失败，不生成内容为空但格式合法的 GLB；
- 目标文件已存在时明确失败，避免静默覆盖。

## 限制

- 不支持 CRP v3；
- 不支持 `ColossalFramework.UI.UITextureAtlas`；
- 只导出主渲染模型，忽略 LOD（远景替换）网格；
- 当前 GLB 材质使用基础色与透明遮罩，不重现游戏的法线、光照、颜色变化和全部专用 Shader 效果；
- 空根网络资产会选择一个代表路段或节点，不会生成游戏运行时动态拼接出的完整道路网络；
- 转换器验证文件结构和资源引用，但不能替代逐资产视觉验收。

## 环境要求

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

不需要安装 *Cities: Skylines*、Unity、ModTools 或 Blender。

## 构建

```powershell
dotnet build .\OfflineCrpConverter.csproj -c Release
```

也可以生成依赖系统 .NET 8 Runtime 的 Windows x64 发布目录：

```powershell
dotnet publish .\OfflineCrpConverter.csproj -c Release -r win-x64 --self-contained false
```

## 用法

```text
OfflineCrpConverter --crp <输入.crp> --output <输出.glb> [--name <资产名>]
```

从构建结果运行：

```powershell
dotnet .\bin\Release\net8.0\OfflineCrpConverter.dll `
  --crp "D:\WorkshopAssets\Example.crp" `
  --output "D:\Exports\Example.glb" `
  --name "Example"
```

也可以直接通过项目运行：

```powershell
dotnet run --project .\OfflineCrpConverter.csproj -- `
  --crp "D:\WorkshopAssets\Example.crp" `
  --output "D:\Exports\Example.glb"
```

`--name` 只设置 GLB 场景名；省略时使用 CRP 中识别到的主资产名。

建议将批量转换结果先写入单独的待验收目录，完成结构检查和逐项视觉检查后，再替换正式资产。

## 权利说明

本项目与 Colossal Order 或 Paradox Interactive 无关联。请只转换你有权处理的资产，并遵守游戏、
创意工坊项目及其作者适用的许可条款。

当前仓库不附带开源许可证。公开可见不等于授权复制、修改或再分发；如需开放这些权利，应另行添加
明确的许可证文件。
