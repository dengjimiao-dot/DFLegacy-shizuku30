# DFLegacy 代理规则

- 禁止直接启动 `DNF.exe`。
- 代理启动客户端前，必须以等待进程结束的方式运行
  `dist\DFLegacy.Server\DFLegacy.Launcher.exe --check-d3d9 --no-dialog`，
  并确认退出码为零。
- 客户端只能通过 `DFLegacy.Launcher.exe` 启动。D3D9 预检返回非零时，
  不得重试启动，也不得改用更弱的显示器判断条件；应直接报告当前没有可用显示设备。
- Launcher 不得读取、校验或修改 `DNF.exe` 的进程内存。客户端补丁由
  `DFLegacy.Ijl15` 在进程附加时负责；`DisableDebuggerDetection` 是该原生模块的
  编译期开关，不得添加回 `launcher.json`。
- 用户手动启动客户端不属于代理自行启动，不受以上规则限制。

## 文档分工

- `README.md` 面向使用者，介绍项目、模块、使用方法、实现范围与已知缺口；不要塞入代理指令、调试地址或联调流水账。
- 代理操作约束、协议维护细节与开发验证说明放在本文件；专项取证保留在相应文档或代码注释中。

## 协议与客户端边界

- 项目只适配 DF2008 / 13338 客户端布局，不兼容原来的 55 版客户端。启动器不检查 PE 文件版本或 SHA256，不代表支持其他客户端。
- 启动参数见 `src/DFLegacy.Launcher/Program.cs`：使用原生模式 `99`，以 `?` 分隔 13 个字段。修改参数时核对客户端解析顺序，不要套用其他版本的启动格式。
- 使用原生中文客户端，不执行汉化、转区或区域设置修改。启动流程不修改磁盘上的 `DNF.exe`、`Script.pvf`、注册表、hosts 或防火墙；安装 `ijl15.dll` 和缺失的默认 `Config.ini` 属于已有安装行为。IJL15 修改当前进程内存，不写回 EXE 文件。
- 13339 的业务实现可以作为参考，包 index、字段排列及客户端显示行为必须回到目标 13338 客户端取证，不要直接套用新版协议。
- 入口协议已有 `11 -> 12`、`5 -> 6`、`9 -> 10`、`1 -> 3`，频道元数据包含九组及 `anton`。
- 游戏帧头为 10 字节，涉及命令序号、CRC32 和 15 位滚动种子恢复；上行字节变换为 `ROR(plain XOR key)`，下行为固定 `ROL6`、`XOR B5`。
- DF2008 的冲突命令号包括出售物品 `24`、物品交易状态 `26`、技能栏变更 `30`，不要照搬其他版本定义。
- UDP 涉及双端口 NAT 反射、CMD 2 端点登记、NOTI 11/26 协商、MTUPD 与 CNRD。`MonsterObjectType` 保持 `529`（`0x211`），客户端本地发包端口为 `5063`。
- 房间重访复用地图与运行时 ID，保留击杀、破坏和未拾取掉落状态；不要通过重新抽选或重新发放奖励来恢复显示。
- IJL15 保持 IJL 1.5 的 `__stdcall` 导出、序号及 `JPEG_CORE_PROPERTIES` ABI；当前 WIC 桥接覆盖客户端实际使用的文件/内存参数读取、整图解码和编码，不代表实现所有 IJL 接口能力。
- 客户端内部渲染保持 `640×480`，外层窗口缩放与 D3D8 采样策略不等于改变内部渲染分辨率，也不是 XBRZ 实现。

## 掉落与副本维护线索

- 普通怪物掉落有金币、消耗品、装备、配方和神器五条独立分支，主要入口为 `src/DFLegacy.Server/DropCatalog.cs`；不要合并成互斥的一次抽选。
- 精英专属表包括 MOB 的 `[common champion drop item]` 和 `[super champion drop item]`，见 `src/DFLegacy.Server/MonsterChampionDropCatalog.cs`。不要将专属掉落误当作普通掉落表的替代品。
- 深渊地图、封印门与恶魔配置见 `src/DFLegacy.Server/DungeonCatalog.cs`，深渊装备表 `itemdropinfo_monster_hell.etc` 见 `src/DFLegacy.Server/HellDropCatalog.cs`；进入时的世界地图门票扣除、深渊 `START_MAP` 状态及恶魔死亡追加掉落在 `EntranceService.cs` 中联动处理。
- 隐藏副本解锁见 `src/DFLegacy.Server/HiddenDungeonUnlockCatalog.cs`。当前兼容映射为任务 `4/116 -> 9`、`363 -> 17`、`66 -> 51`、`203 -> 1000`、`428 -> 1500`，另有结合任务与副本目录的解析。权限需持久化，并在角色初始化、相关任务接受及副本选择时同步；修改前核对当前源码与 PVF，不将这些编号当作所有资源版本的固定规则。
- 房间掉落恢复同时核对 `DungeonGroundItemState.cs`、`DungeonRunState.cs` 和 `EntranceService.cs`，区分恢复已有物品与产生新掉落，不重复抽选或发放。

## 身份与随机数

- 认证后以服务端 `AccountUid` 识别账号，不信任客户端传入的 UID。
- UID `1` 到 `10,000,000` 为测试账号区间，在创建或旧数据迁移时幂等初始化预置角色；`10,000,001` 到 `17,999,999` 为保留/历史区间；普通新账号从 `18,000,000` 开始，不创建测试预置角色。
- `CharacterNo` 是全局自增角色业务 ID，删除后不复用；`Slot` 是角色列表槽位；`CharacterRecord.Id` 是 JSON 存储 Guid，三者不能混用。
- `OicqCode` 用于找回密码，修改密码校验原密码；不要把这些凭据或测试数据写入公开文档示例。
- 玩法随机使用 `GameRandomSource`：检查 CPU、架构及原生桥接可用性后使用 RDSEED，失败回退到操作系统 CSPRNG，再失败才使用 `Random.Shared`。有上界随机数使用拒绝采样。
- `EquipmentQualitySeed` 是客户端解释的 32 位品级字段，不是托管 PRNG 的初始化种子。生成和重抽遵循业务随机入口；商店品级遵循每日统一种子规则。
- 协议密钥、实体 GUID、商城 Web 票据继续使用系统密码学随机源，不接入允许普通 PRNG 回退的玩法随机链。

## 构建与验证

- 依赖 .NET 10 SDK、Visual Studio C++ 工具链和 CMake。混合解决方案包含 Win32 IJL15 和 x64 RandomNative，使用完整 Visual Studio MSBuild；不要将 `dotnet build` 对 VC 工程的失败当作托管代码错误。
- `RandomNative.vcxproj` 不是托管程序集引用。独立托管构建可不带原生随机桥接，运行时必须保留随机源回退。
- 按变更范围执行验证，以下为完整开发验证入口：

```powershell
msbuild /m /restore /p:Configuration=Release .\DFLegacy.Emulator.slnx
.\src\DFLegacy.RandomNative\build-native.ps1 -Configuration Release -RunTests
dotnet run --project .\tests\DFLegacy.SmokeTests\DFLegacy.SmokeTests.csproj -c Release
.\src\DFLegacy.Ijl15\build-native.ps1 -Configuration Release -RunTests
```

- PVF 冒烟测试覆盖关键目录、商城、副本、装备、技能、任务、掉落、深渊和房间重访。不要把历史 PVF 文件数或用户正常修改的条目当作永恒基线，更不要为通过断言还原用户 PVF。
- `--self-test --no-dialog` 只检查启动器文件和配置；`--install-ijl15 --no-dialog` 只安装补丁，均不能代替启动客户端前的 D3D9 预检。
- 修改文档不需要重启服务端或客户端；发布代码时核实实际运行目录和数据路径，保留账号角色数据。
