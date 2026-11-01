# OfflineCrpConverter

`OfflineCrpConverter` 是一个独立的 .NET 8 命令行工具（CLI），用于将
*Cities: Skylines* 的 `.crp` 资产转换成纹理内嵌的 glTF 2.0 Binary（`.glb`）。

它直接解析 CRP，不启动游戏、不加载游戏或 ModTools DLL、不调用 Blender。每次成功转换只生成一个
`.glb`；不会附带 OBJ、MTL、FBX、来源记录或校验报告。

## 当前能力

- 支持 CRP 格式版本 3–10；
- 解析 `BuildingInfo`、`PropInfo`、`TreeInfo`、`VehicleInfo` 和 `NetInfo` 的主渲染几何；
- 按 checksum 连接 `MeshFilter`、`MeshRenderer` 材质槽和材质贴图；
- 递归导出建筑的 `m_subMeshes` 与 `m_subBuildings`；
- 按 CRP 资产表名称或 checksum 解析附属建筑引用，并排除附属建筑以确定主模型；
- 使用 `--workshop` 指定本地订阅根目录时，递归载入建筑 `m_props` 中以工坊 ID 限定的道具与树木，保留位置和角度；缺失依赖、循环依赖或非 100% 的随机放置明确失败；
- 用户选择 `--decal-ground <高度>` 时，将地形贴花的水平朝上表面保留源 UV 并移到指定局部地面高度，删除投影体积侧面；用于明确选择平地静态化的资产，不是默认提取行为；
- 用户明确选择 `--exclude-prefab <完整引用名>` 时，排除该工坊道具的所有放置实例；精确区分大小写，不接受通配符。选项可重复，未匹配任何引用时失败，未排除的缺失依赖仍然失败；
- 解析网络资产的车道、路段和节点，并在空网络根节点中选择可渲染的代表模型；
- 保留材质槽顺序，跳过 Unity Mesh 中合法的空子网格；
- 将 Unity 左手坐标系转换为 glTF 右手坐标系，并同步修正三角形绕序；
- 保留源 UV，不执行旧 OBJ/Blender 工作流常见的额外 V 翻转；
- 将 DDS 漫反射贴图转换为内嵌 PNG；
- 将建筑和道具 Shader 使用的反相 `ACIMap.R` 合成到基础色 alpha，并使用 `MASK`；
- 将 `Custom/Trees/Default` 使用的反相 `XYCAMap.A` 合成到基础色 alpha，并使用 `MASK`，保留双面叶片；
- 默认自动适配 `Custom/Trees/Default` 的静态基础色，不需要另传校色参数或外部配置。先合成 alpha，再只统计可见像素，按材质分别计算保护亮部的阴影曲线和有限的饱和度调整；控制台输出实际采用的参数；
- 可显式指定 `--tree-color-exposure <EV>` 与 `--tree-color-saturation <倍率>`，将按源预览校准的静态颜色烘焙到树木基础色贴图。只影响 `Custom/Trees/Default`，在线性光空间调整 RGB，不改 alpha、UV、网格或源色彩空间标记；两个参数必须同时提供；
- 将材质设为 `doubleSided`，避免单面旧资产因观察方向不同而消失；
- 在写盘前回读 GLB，验证容器、场景、网格、图元、材质、bufferView 与内嵌 PNG；
- 主模型没有可导出几何时明确失败，不生成内容为空但格式合法的 GLB；
- 目标文件已存在时明确失败，避免静默覆盖。

## 限制

- 不导出 `ColossalFramework.UI.UITextureAtlas`；树木、网络资产及已经读完几何引用字段的建筑，其纯 UI 尾部按 CRP 资产边界跳过；其他需要解析图集的资产仍会明确报错；
- 不把 `Custom/Props/Decal/*` 的地形投影包围盒当作静态几何导出；未提供 `--decal-ground` 时明确失败。平地转换不适应斜坡，也不重现游戏的动态地形投影；没有带源 UV 的水平顶面或混合贴花/实体材质的网格仍明确失败；
- 只导出主渲染模型，忽略 LOD（远景替换）网格；
- 当前 GLB 材质使用基础色与透明遮罩，不重现游戏的法线、光照、颜色变化和全部专用 Shader 效果；
- 默认树木颜色适配是静态 PBR 外观近似，不是完整的游戏着色器移植，也不代表所有灯光下都与游戏一致。仍需要逐资产视觉验收；树木以外的材质不应用这项适配；
- 没有可见像素或可见像素亮度中位数为零时，自动模式明确失败，不写出貌似成功的 GLB。不能凭空恢复纯黑贴图的颜色，可检查源数据后显式选择原色或手动校准；
- 树木导出静态主网格，不导出游戏生成的公告板 LOD、风摆或运行时随机树种替换；
- 不包含没有工坊 ID 的游戏内置装饰（例如现代泛光灯）；它们不属于离线订阅 CRP。建筑主体依赖的工坊道具不能忽略；没有提供 `--workshop` 时明确失败；
- 空根网络资产会选择一个代表路段或节点，不会生成游戏运行时动态拼接出的完整道路网络；
- 转换器验证文件结构和资源引用，但不能替代逐资产视觉验收。

## 环境要求

- 运行已发布的 CLI 需要 [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)；从源码构建需要 .NET 8 SDK。

不需要安装 *Cities: Skylines*、Unity、ModTools 或 Blender。

当前依赖 `SixLabors.ImageSharp 3.1.12`，构建仍报告 6 项已知安全公告（4 高、2 中）。
本次材质修复未升级该依赖，不是完成安全加固的版本；请勿用它处理来源不可信的输入。

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
OfflineCrpConverter --crp <输入.crp> --output <输出.glb> [--name <资产名>] [--workshop <本地订阅CRP根目录>] [--decal-ground <贴花局部地面高度>] [--exclude-prefab <完整工坊道具引用名>] [--tree-color-mode auto|raw]
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

树木默认使用 `auto` 模式。自动适配尊重 CRP 的 `ForceLinear` 标记，再将结果编码成 glTF 基础色要求的 sRGB。
算法使用 4096 档对数直方图，统计可见像素的线性 RGB 最大通道（HSV value），以 0.14 为中位数目标，计算 `gamma = log(0.14 / 0.30) / log(源中位数 / 0.30)`。
最大通道低于 0.30 时使用 `0.30 * (通道峰值 / 0.30)^gamma` 求增益，换算成 sRGB 增益后同比例缩放 RGB；阴影曲线本身不调整较亮的像素。
使用通道峰值而非 Rec.709 亮度，避免将红叶或蓝叶提到和绿叶相同亮度时造成过曝。自动模式在 sRGB 调色板中限制饱和度增益，并同比例约束超出色域的值，保留原来的 RGB 色相。手动曝光模式仍在线性 RGB 中计算。
仅在提升偏暗贴图时适量恢复色度，倍率限制在 1 到 1.75 之间；亮度原本达到目标的贴图使用恒等曲线和原始饱和度。
算法不根据文件名、工坊 ID、校验和或预设文件选择参数，也不会把红叶、黄叶或白花强制染成绿色。

需要保留未经适配的原始 RGB 时，必须显式加 `--tree-color-mode raw`。
需要人工指定树木静态颜色校准时，另加 `--tree-color-exposure <EV> --tree-color-saturation <倍率>`，替代自动模式。
曝光范围为 -16 到 16 EV，饱和度范围为 0 到 8，均要求有限数值。
校准作用于解码后的线性 RGB：先按 Rec.709 亮度调整饱和度，再乘 `2^EV`，最后编码回 sRGB。
不添加自发光、不改变叶片透明度，也不会从游戏预览中自动猜测参数。
显式 `--tree-color-mode` 与手动参数不能混用；未知参数、重复参数和不完整的手动参数明确报错。

建议将批量转换结果先写入单独的待验收目录，完成结构检查和逐项视觉检查后，再替换正式资产。

## 默认颜色路径回归测试

`tests` 目录是无额外测试框架依赖的测试程序，从仓库根目录可直接运行：

```powershell
dotnet run --project .\tests\OfflineCrpConverter.Tests.csproj -c Release
```

使用运行时生成的纹理和网格，覆盖真正的默认 GLB 构建路径、显式原色、非树木不变、红/绿/蓝/灰暗色、
防过曝、透明遮罩合成顺序、可见像素统计、线性色彩空间、亮色恒等和异常输入；不需要任何工坊资源。

## 权利说明

本项目与 Colossal Order 或 Paradox Interactive 无关联。请只转换你有权处理的资产，并遵守游戏、
创意工坊项目及其作者适用的许可条款。

当前仓库不附带开源许可证。公开可见不等于授权复制、修改或再分发；如需开放这些权利，应另行添加
明确的许可证文件。
