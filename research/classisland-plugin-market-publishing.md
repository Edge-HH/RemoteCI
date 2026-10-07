# ClassIsland 2.x 插件市场上架清单调研

调研日期：2026-08-30 喵～

范围：仅核对 ClassIsland 官方文档、官方插件索引仓库和官方索引生成器，不替项目填写具体值喵～

## 结论

ClassIsland 2.x 插件市场使用 YAML 市场清单，文件名必须是插件 ID 加 `.yml`，提交位置是 `ClassIsland/PluginIndex` 仓库的 `index/plugins-v2/`，随后向该仓库发起 PR 并等待审核喵～

市场清单是在插件原有 `manifest.yml` 的基础上增加仓库和 Release 信息，不是另一套完全独立的元数据喵～

## 字段

### 上架后合并计算的必填字段

| 字段 | 类型 | 来源与含义 |
| --- | --- | --- |
| `id` | `string` | 基础清单必填，插件唯一 ID 喵～ |
| `entranceAssembly` | `string` | 基础清单必填，入口程序集文件名喵～ |
| `apiVersion` | `Version` / 市场表写作 `string` | 基础清单和市场补充表都标为必填；2.x 会拒绝低于 `2.0.0.0` 的值喵～ |
| `repoOwner` | `string` | 市场补充必填，GitHub 仓库所有者喵～ |
| `repoName` | `string` | 市场补充必填，GitHub 仓库名喵～ |
| `assetsRoot` | `string` | 市场补充必填，格式为 `<默认分支>/<插件项目相对仓库路径>` 喵～ |
| `version` | `Version` / 市场表写作 `string` | 基础清单中可选，但市场补充表明确要求必填，因此上架清单应填写喵～ |
| `author` | `string` | 基础清单中可选，但市场补充表明确要求必填，因此上架清单应填写喵～ |

来源：[发布插件：上架字段表](https://docs.classisland.tech/dev/plugins/publishing.html#%E4%B8%8A%E6%9E%B6%E5%88%B0%E6%8F%92%E4%BB%B6%E5%B8%82%E5%9C%BA)、[开始编写插件：基础清单字段表](https://docs.classisland.tech/dev/plugins/create-project.html#%E6%8F%92%E4%BB%B6%E6%B8%85%E5%8D%95%E6%96%87%E4%BB%B6) 喵～

### 选填字段

| 字段 | 类型 | 规则 |
| --- | --- | --- |
| `name` | `string` | 插件显示名称喵～ |
| `description` | `string` | 插件描述喵～ |
| `url` | `string` | 插件主页 URL 喵～ |
| `icon` | `string` | 图标文件名，默认 `icon.png` 喵～ |
| `readme` | `string` | 自述文件名，默认 `README.md` 喵～ |
| `artifactName` | `string` | 指定 Release 中要下载的 `.cipx` 文件名；不填时匹配第一个以 `.cipx` 结尾的工件喵～ |
| `tagPattern` | `string` | 限定查找 Release 时匹配的 Tag 模式喵～ |
| `supportedOSPlatforms` | `List` | 可用文档示例值为 `Windows`、`Linux`、`OSX`；未声明时三个平台均不显示警告，声明外的平台只显示警告而不阻止安装喵～ |

基础模型还公开了 `dependencies` 列表，但当前“发布插件”页面没有把它列为市场补充字段；是否需要它应以插件自身依赖声明为准，不应为了上架凭空添加喵～

来源：[基础清单字段表](https://docs.classisland.tech/dev/plugins/create-project.html#%E6%8F%92%E4%BB%B6%E6%B8%85%E5%8D%95%E6%96%87%E4%BB%B6)、[市场补充字段表](https://docs.classisland.tech/dev/plugins/publishing.html#%E4%B8%8A%E6%9E%B6%E5%88%B0%E6%8F%92%E4%BB%B6%E5%B8%82%E5%9C%BA)、[当前 `PluginManifest` 源码](https://github.com/ClassIsland/ClassIsland/blob/master/ClassIsland.Core/Models/Plugin/PluginManifest.cs) 喵～

## 文件格式和位置

- 格式是 YAML，扩展名应为 `.yml`；官方索引生成器只枚举目录顶层的 `.yml` 文件喵～
- 文件名应重命名为插件 ID，例如 ID 为 `classisland.example` 时，文件名为 `classisland.example.yml` 喵～
- ClassIsland 2.x 的目标目录是官方 [`index/plugins-v2/`](https://github.com/ClassIsland/PluginIndex/tree/main/index/plugins-v2) 喵～
- `assetsRoot` 指向 GitHub 仓库默认分支中的插件资源目录，索引生成器用它拼接 `readme` 和 `icon` 的原始文件地址喵～

来源：[发布插件文档](https://docs.classisland.tech/dev/plugins/publishing.html#%E4%B8%8A%E6%9E%B6%E5%88%B0%E6%8F%92%E4%BB%B6%E5%B8%82%E5%9C%BA)、[索引生成器的 YAML 枚举与解析代码](https://github.com/ClassIsland/ClassIsland.PluginIndexGenerator/blob/1.1.1.0/ClassIsland.PluginIndexGenerator/Abstractions/Generators/MarketplaceIndexGeneratorBase.cs)、[插件索引生成代码](https://github.com/ClassIsland/ClassIsland.PluginIndexGenerator/blob/1.1.1.0/ClassIsland.PluginIndexGenerator/Generators/PluginIndexGenerator.cs) 喵～

## 上架流程

1. 确认插件内容合法合规、项目符合开源定义且带开源许可证，并托管在 GitHub 喵～
2. 打包 `.cipx`；官方推荐在插件项目目录执行 `dotnet publish -p:CreateCipx=true`，产物和校验信息输出到 `cipx` 目录喵～
3. 在插件自己的 GitHub 仓库创建 Release 并上传 `.cipx` 喵～
4. Release Tag 必须严格为 `a.b.c.d`，例如 `1.2.3.4`，不能添加 `v` 等其它内容喵～
5. Release 正文必须带对应工件的 MD5；自动打包已生成时可直接使用，否则按官方格式写入 `<!-- CLASSISLAND_PKG_MD5 {"插件包文件名": "MD5"} -->` 喵～
6. 将补全后的市场清单以 `<插件 ID>.yml` 放到 fork 的 `index/plugins-v2/` 喵～
7. 向 `ClassIsland/PluginIndex` 发起 PR；官方工作流会运行索引生成器校验，审核通过并合并后进入插件市场喵～

索引生成器会先按 `tagPattern` 过滤 Release，再只保留可解析为版本号的 Tag，取版本号最大的 Release，然后按 `artifactName` 精确匹配或选择第一个 `.cipx` 工件，并把最终索引中的 `version` 改为该 Release Tag 喵～

即使生成器会从 Release Tag 回填版本，发布文档仍把 `version` 标为必填，描述文件不应因此省略它喵～

来源：[发布插件文档](https://docs.classisland.tech/dev/plugins/publishing.html)、[官方 PR 校验工作流](https://github.com/ClassIsland/PluginIndex/blob/main/.github/workflows/validate-pr.yml)、[1.1.1.0 版索引生成器](https://github.com/ClassIsland/ClassIsland.PluginIndexGenerator/blob/1.1.1.0/ClassIsland.PluginIndexGenerator/Abstractions/Generators/MarketplaceIndexGeneratorBase.cs)、[MD5 提取实现](https://github.com/ClassIsland/ClassIsland.PluginIndexGenerator/blob/1.1.1.0/ClassIsland.Core/Helpers/ChecksumHelper.cs) 喵～

## 官方示例与 Schema 状态

- 文档内完整示例：[`classisland.example.yml`](https://docs.classisland.tech/dev/plugins/publishing.html#%E4%B8%8A%E6%9E%B6%E5%88%B0%E6%8F%92%E4%BB%B6%E5%B8%82%E5%9C%BA) 喵～
- 插件索引仓库中的实际示例：[`index/plugins-v2/classisland.example.yml`](https://github.com/ClassIsland/PluginIndex/blob/main/index/plugins-v2/classisland.example.yml) 喵～
- 其它已合并的 2.x 清单可在 [`index/plugins-v2/`](https://github.com/ClassIsland/PluginIndex/tree/main/index/plugins-v2) 对照喵～
- 截至调研日期，在官方 `PluginIndex` 和 `ClassIsland.PluginIndexGenerator` 仓库文件树中未发现独立的 JSON Schema 或 YAML Schema 文件，因此没有可提供的官方 Schema URL 喵～
- 当前可视作权威结构说明的是发布文档字段表、[`PluginManifest`](https://github.com/ClassIsland/ClassIsland/blob/master/ClassIsland.Core/Models/Plugin/PluginManifest.cs)、[`PluginRepoManifest`](https://github.com/ClassIsland/ClassIsland.PluginIndexGenerator/blob/1.1.1.0/ClassIsland.Core/Models/Plugin/PluginRepoManifest.cs) 和实际运行的 PR 校验工作流喵～

## 已发现的官方资料差异

官方发布文档列出了 `supportedOSPlatforms`，当前 ClassIsland 主仓库的 `PluginManifest` 也实现了该字段，但插件索引仓库工作流固定下载的索引生成器 `1.1.1.0` 内置模型没有该字段，并且 YAML 解析器会忽略未匹配字段喵～

这是官方资料与当前校验工具源码之间可直接观察到的差异，不在此推断最终市场 UI 行为；若上架必须依赖操作系统警告，应在 PR 中向维护者确认喵～

来源：[发布字段表](https://docs.classisland.tech/dev/plugins/publishing.html#%E4%B8%8A%E6%9E%B6%E5%88%B0%E6%8F%92%E4%BB%B6%E5%B8%82%E5%9C%BA)、[ClassIsland 当前模型](https://github.com/ClassIsland/ClassIsland/blob/master/ClassIsland.Core/Models/Plugin/PluginManifest.cs)、[PluginIndex 校验工作流固定版本](https://github.com/ClassIsland/PluginIndex/blob/main/.github/workflows/validate-pr.yml)、[生成器 1.1.1.0 模型](https://github.com/ClassIsland/ClassIsland.PluginIndexGenerator/blob/1.1.1.0/ClassIsland.Core/Models/Plugin/PluginManifest.cs) 喵～
