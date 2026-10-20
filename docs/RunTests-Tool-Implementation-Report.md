# run_tests 工具实现进度报告

> 日期：2026-10-20　分支：`feature/run-tests-tool`　范围：新增测试工具 + 验证策略改为「测试优先」
> 结论速览：**.NET 与 C++ 测试均已可用**。.NET 走 `dotnet test`（VSTest + TRX），
> C++ 走 CMake/CTest（JUnit 输出）；两者共用同一套工具、结果渲染与验证流程。

---

## 一、为什么要做

此前 Agent 跑不了测试，只能靠终端：

| 途径 | 问题 |
|--|--|
| `run_in_terminal` 执行 `dotnet test` | 命令可能被安全过滤器误伤；控制台输出要人工解析；与「编译走 build_solution」的分工不一致 |
| （不存在）专用测试工具 | — |

VS 侧没有等价于 `IVsSolutionBuildManager` 的公开测试接口，因此实现上复用 **VS Test Platform 的同一引擎**
（`dotnet test` → VSTest）；C++ 侧没有等价接口，改走 **CMake/CTest**。
两边的结果都从结构化日志取（.NET 用 **TRX**、C++ 用 **JUnit XML**），不解析控制台文本（避免受本地化输出影响）。

## 二、已完成

### 2.1 服务层

| 文件 | 行数 | 职责 |
|--|--|--|
| [ITestService.cs](../Services/Interfaces/ITestService.cs) | 101 | 接口 + `TestRunResult` / `TestRunErrorKind` / `FailedTestInfo` 模型 |
| [TestService.cs](../Services/TestService.cs) | 482 | 项目发现、dotnet 定位、执行、超时与进程树清理 |
| [TestService.Trx.cs](../Services/TestService.Trx.cs) | 101 | TRX（XML）解析：计数、失败明细、耗时 |
| [TestService.CMake.cs](../Services/TestService.CMake.cs) | 1220 | C++ 支持：CMake 工程发现、preset 解析、构建目录选择、ctest 执行、MSVC/ASan 环境注入 |
| [TestService.JUnit.cs](../Services/TestService.JUnit.cs) | 305 | CTest JUnit（XML）解析：计数、失败明细（含从 `system-out` 摘取断言详情）、耗时 |
| [RunTestsTool.cs](../Services/BuiltInTools/RunTestsTool.cs) | 237 | 工具定义、结果渲染、失败判定 |

**项目发现**：按内容特征匹配 `Microsoft.NET.Test.Sdk`、`xUnit`、`NUnit`、`MSTest`，
覆盖 `*.csproj / *.fsproj / *.vbproj`；跳过 `bin/`、`obj/` 下的中间项目文件。

**dotnet 定位**（三级回退）：`PATH` → `%ProgramFiles%\dotnet` → `vswhere` 定位的 VS 自带 `dotnet`。

**执行策略**：

```
dotnet test <项目> --nologo --logger "trx;LogFileName=<临时文件>"
  [--filter <表达式>] [--configuration <配置>]
```

- **自带构建**：不带 `--no-build`，`dotnet test` 会先编译测试项目及其依赖再跑用例，
  因此「跑测试」本身就是一次完整验证，不需要在计划里单列构建步骤。
- 超时 10 分钟，超时/取消时用 `taskkill /T` 清理子进程树（net472 无 `Process.Kill(entireProcessTree)`）。
- 进程输出显式指定 UTF-8 编码，避免重定向时诊断文本乱码。

**结果解析**：只认 TRX。`Failed / Timeout / Aborted` 一律计入失败，
`NotExecuted / Skipped / Inconclusive` 计入跳过；失败消息优先 `Message`、回退 `StackTrace`，
归一化为单行并截断到 400 字符。

**失败判定**（供上层验证状态使用）：采用白名单式失败判定——
只有 `failed>0`、`Error:`、`Timeout:` 才算失败，其余视为通过。理由是把成功误判为失败
会让系统多跑一次昂贵的构建，代价高于漏判（漏判时最终答案仍带测试输出供人工核对）。

### 2.2 工具与接入

- 工具名 `run_tests`，参数 `project` / `filter` / `configuration`
- 输出首行是稳定摘要（不随语言变化）：`total=N passed=N failed=N skipped=N elapsed=Ns`，
  其后是失败用例清单；无测试项目/多项目场景带稳定前缀 `NO_TESTS:` / `MULTIPLE_TEST_PROJECTS:`
- 注册：`BuiltInToolService` + DI（`ServiceCollectionExtensions`）+ WPF 控件手工构造路径
- 白名单：`EditAgent`、`BuildAgent`、`KnownBuiltInToolNames`、`IsInteractiveTool`
- 终端拦截：`dotnet test` / `vstest.console` 提示改用 `run_tests`，并单独区分测试/构建两类文案
- 本地化：中英各补齐 `tool.run_tests.desc`、参数、结果与错误文案

### 2.3 验证策略：测试优先、构建兜底

`EditAgent` 的验证状态从「只看构建」扩展为「测试优先」：

```
改完 ──► run_tests ──┬─ 通过 ──────────────► 结束验证
                     ├─ 失败 ──────────────► 交 Build Agent
                     └─ 无测试项目 ────────► build_solution 兜底 ──► 通过则结束
```

- 新增状态 `_didAttemptTests` / `_lastTestsPassed` / `_lastDirectTestResult`，与既有构建状态并列
- 文件在验证后再改动时，**测试与构建结果一起失效**
- `ShouldHandoffToBuild` 增加测试维度：测试通过 → 不移交；测试失败或无测试 → 看构建结果
- Handoff 给 Build 的提示词改为测试优先，并附带上一次测试结果，避免重复跑同一组测试

### 2.4 步骤级许可同时覆盖测试

原先只认「构建/编译」关键词，标题写「运行单元测试」的中间步骤会被拦下 `run_tests`。现在：

- 中文关键词：`构建 / 编译 / 生成解决方案 / 验证构建 / 构建验证 / 重新构建 / 测试 / 跑测试 / 运行测试 / 单元测试 / 验证`
- 英文关键词：`build / compile / rebuild / msbuild / dotnet build / test / tests / ctest / verify / verification`（按词边界匹配）
- 日志：`步骤N 允许构建/测试（原因）`、`步骤N 未要求构建/测试且非最后一步，已禁用 build_solution / run_tests`
- 判据仍只看 `Title`，描述里的「确保单元测试通过」这类约束性说法不放行

### 2.5 已修复的缺陷

| 缺陷 | 现象 | 根因 | 修复 |
|--|--|--|--|
| 测试服务漏注入 | `run_tests` 永远返回「测试服务未初始化」 | WPF 控件直接 `new BuiltInToolService(...)` 绕过 DI，且漏传 `ITestService` | 在该构造路径显式注入 `TestService`，并补回归测试 |
| 跑测试被拆成两步 | 计划里出现独立的「构建解决方案」步骤 | `run_tests` 使用 `--no-build`，必须先调 `build_solution` | 去掉 `--no-build`，改为自带构建；提示词明确「不要拆成构建+跑测试两步」 |

## 三、测试与验证

- 新增单测 46 项：项目发现（含 `obj/` 排除、`.sln` 入参）、多项目/无项目分支、
  TRX 计数与失败明细（含 `StackTrace` 回退、超长截断）、结果渲染与摘要、
  测试优先的移交判定（7 组组合）、步骤许可测试意图矩阵；
  以及 C++ 侧的 CMake 工程发现/排除、preset 继承与宏展开、构建目录选择、
  MSVC 环境判断与环境块解析、JUnit 与 ctest 文本结果解析、`ctest` 终端拦截
- 端到端实测：对真实测试项目从零构建 + 运行，`total=13 passed=12`（1 项为并行竞态导致，
  已确认与本工具无关），耗时约 17~20s
- 全量回归 **2117 项通过**；两个语言文件通过 `System.Text.Json` 严格解析

## 四、C++ 测试（已实现）

实测项目 `F:\CPP\database`（C++ + CMake(Ninja/cl.exe) + GoogleTest，`gtest_discover_tests` 注册）
已能直接走 `run_tests`，不再需要终端。

### 4.1 实现方式

1. **识别**：扫描工作区内的 `CMakeLists.txt`，内容命中
   `enable_testing(` / `include(ctest)` / `add_test(` / `gtest_discover_tests(` /
   `catch_discover_tests(` / `doctest_discover_tests(` / `qt_add_test` 之一即认定为测试工程。
   跳过 `build/`、`out/`、`cmake-build-*`、`_deps/`、`third_party/` 等构建产物与依赖目录；
   子目录工程若被根工程 `add_subdirectory` 引入，只保留最外层工程根，
   否则项目数虚高、每次都要显式指定 `project`。
2. **选构建目录**（`ResolveCmakeBuildTarget`）：
   - `configuration` 参数显式点名的 preset（同时支持把 `Debug` / `Release` 当配置名）
   - `CMakePresets.json` 里**已经配置过**（含 `CMakeCache.txt`）的非 hidden preset
   - 首个非 hidden preset
   - 既有 `build/`、`out/build/*`、`cmake-build-*` 目录
   - 兜底 `<工程根>/build`（首次运行时触发一次 `cmake --preset` / `-S -B` 配置）
   - preset 的 `inherits` 链会被展开，`${sourceDir}` / `${presetName}` 等宏按**子 preset 名**展开，
     这样 `binaryDir` 为 `${sourceDir}/out/build/${presetName}` 时不会落到父 preset 的目录
3. **执行**：
   ```
   cmake --preset <preset>            # 仅当目标构建目录尚未配置时
   cmake --build <buildDir> [--config <cfg>]   # 自带增量构建，失败即判定 BuildFailed
   ctest --test-dir <buildDir> --output-on-failure
         --output-junit <临时文件> [-R <filter>] [-C <cfg>]
   ```
   `--output-junit` 需要 CMake 3.21+；探测不到时退回解析 ctest 的汇总文本
   （`N% tests passed, M tests failed out of T` 等，ctest 输出不随语言本地化，匹配是稳定的）。
4. **MSVC / AddressSanitizer 环境**：检测到构建缓存或预设使用 `cl.exe`，或者开启了
   `/fsanitize=address` 时，执行 `vcvars64.bat` 并捕获其环境变量注入 configure/build/ctest 进程。
   实测该项目的 157 个用例原本会全部以 `0xc0000135` STATUS_DLL_NOT_FOUND 失败
   （ASan 运行时 `clang_rt.asan_dynamic-x86_64.dll` 就在 MSVC `bin` 目录下、不在默认 PATH），
   注入后正常。
5. **收口**：终端拦截 `ctest`（按词边界匹配，`ctest_failed.log` 这类文件名不会误伤），
   指引改用 `run_tests`；`tool.run_tests.desc` 里「C++ 请用终端」的说法已删除。

### 4.2 与 .NET 路径的一致性

两条路径都归一到同一份 `TestRunResult`，因此上层的渲染与判定完全共用：

- 首行稳定摘要 `total=N passed=N failed=N skipped=N elapsed=Ns` 不随语言/技术栈变化
- `TestRunErrorKind.BuildFailed` 表示构建/配置失败（.NET 的构建失败、C++ 的 cmake configure/build 失败）
- `TestRunErrorKind.MultipleProjects` 同样要求显式指定工程（C++ 用 `CMakeLists.txt` 或工程根目录）
- `IsTestRunFailure`、`GetResultSummary`、`EditAgent` 的「测试优先、构建兜底」流程对 C++ 工作区同样成立
- CTest 的 JUnit 里 `message` 常是占位符（`"Failed"`），真正的断言详情写在 `<system-out>`
  （gtest 的 `error: Expected equality of these values:` 等）；解析器会从输出里摘取失败片段，
  否则模型看到的失败原因就只是一句无意义的 `Failed`

### 4.3 实测结果

- 参考工程 `F:\CPP\database`：不带过滤器全量 **157 个用例**，`total=157 passed=145 failed=12`
  （12 项失败为用户工程自身的真实缺陷/环境问题：如 `sql_parser` 未剥离尾分号、
  部分用例依赖非中文用户名下的临时目录；与本工具无关）
- `-R SQLParserTest` 过滤：`total=38 passed=36 failed=2`，失败用例的断言详情被正确带出：

  ```
  SQLParserTest.ParseStripsTrailingSemicolon :: ...sql_parser_test.cpp(304): error: Expected equality of these values:
    static_cast<SelectStmt*>(stmt.get())->table_name Which is: "users;" "users"
  ```
- 全量回归 **2117 项通过**（其中新增 46 项 C++ 相关单测）；两个语言文件通过 `System.Text.Json` 严格解析

### 4.4 其他后续项

顺带修复了 Ask 移交到 Build 后**内部提示词泄漏到 UI 时间线**的问题：Build / Plan / Explore 的
启动日志原先把整段入参（用户请求或 Handoff 提示词）经 `AddLog` 广播，而 `AddLog` 会渲染进过程折叠块，
于是用户会看到本该只给模型的指令（例如测试移交时那句「优先复用对话历史中已读取的文件内容…」）。
现在原文只经 `SummarizeForLog` 压缩后写日志文件，时间线固定使用不含占位符的
`agent.log.*StartedShort` 文案；`SummarizeForLog` 已从 AskAgent 提升到 `BaseAgent` 供四个 Agent 共用。

- 只读 Agent（Ask / Explore）不提供 `run_tests` / `build_solution`：这两个工具虽出现在全体 Agent 共用的工具
  JSON 里（前缀缓存需要固定工具表），但调用会被客户端白名单拒绝。Ask 的提示词已写明这一点，并把**单纯的
  构建 / 测试请求**路由给 Build Agent（它同时持有 `build_solution` 与 `run_tests`），改代码类请求仍交给 Edit Agent
- 覆盖率：VS 安装目录已有 `Microsoft.CodeCoverage.IO.dll` + `datacollector.exe`，
  但产物 vsix 未打包它们，若要出覆盖率需一并处理打包
- 多测试项目场景（.NET 与 C++ 同样）目前要求显式指定 `project`；
  后续可考虑按变更文件反查相关测试项目
- CTest 的 `--output-junit` 需要 CMake 3.21+，更老的 CMake 只能拿到文本汇总（无每用例消息）

## 五、分支状态

`feature/run-tests-tool`（基于 `dev` @ `af2acae`），4 个提交，尚未推送：

```
38092a2  feat(tools): 新增 run_tests 工具，.NET 测试不再走终端
a4c4ed6  feat(agent): 改动后优先跑测试，无测试才构建
07a4bac  fix(agent): run_tests 自带构建，并修掉工具服务漏注入
00f2699  feat(agent): 步骤级许可同时覆盖测试，标题含测试意图即放行 run_tests
```

第四章的 C++ 测试支持（`TestService.CMake.cs` / `TestService.JUnit.cs`、
接口与终端拦截调整、双语本地化、46 项新增单测）目前是工作区改动，尚未提交。

另外两个修复在本分支一并完成（与测试工具无直接依赖，来自实际使用反馈）：

| 提交 | 内容 |
|--|--|
| `f109650` | 选项描述改用正常文本色与字号（issue #109） |
| `cf249e7` | 提问框/审批框/任务面板等适配浅色主题 |
| `fab0b1c` | 自动完成步骤只认正式输出，不再扫思考内容 |
| `af2acae` | 修正 `format` 规则误报，`Get-Date -Format` 不再被拦截 |
