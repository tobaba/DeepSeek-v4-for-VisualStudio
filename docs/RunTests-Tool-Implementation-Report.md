# run_tests 工具实现进度报告

> 日期：2026-10-20　分支：`feature/run-tests-tool`　范围：新增测试工具 + 验证策略改为「测试优先」
> 结论速览：**.NET 测试已可用**（自动发现项目、自带构建、结构化结果、终端拦截、验证流程接入）；
> **C++ 尚未覆盖**（CMake/CTest 与各家 test adapter 未接），仍是本阶段的主要缺口。

---

## 一、为什么要做

此前 Agent 跑不了测试，只能靠终端：

| 途径 | 问题 |
|--|--|
| `run_in_terminal` 执行 `dotnet test` | 命令可能被安全过滤器误伤；控制台输出要人工解析；与「编译走 build_solution」的分工不一致 |
| （不存在）专用测试工具 | — |

VS 侧没有等价于 `IVsSolutionBuildManager` 的公开测试接口，因此实现上复用 **VS Test Platform 的同一引擎**
（`dotnet test` → VSTest），结果从 **TRX 日志**取结构化数据，不解析控制台文本（避免受本地化输出影响）。

## 二、已完成

### 2.1 服务层

| 文件 | 行数 | 职责 |
|--|--|--|
| [ITestService.cs](../Services/Interfaces/ITestService.cs) | 101 | 接口 + `TestRunResult` / `TestRunErrorKind` / `FailedTestInfo` 模型 |
| [TestService.cs](../Services/TestService.cs) | 482 | 项目发现、dotnet 定位、执行、超时与进程树清理 |
| [TestService.Trx.cs](../Services/TestService.Trx.cs) | 101 | TRX（XML）解析：计数、失败明细、耗时 |
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

- 新增单测 21 项：项目发现（含 `obj/` 排除、`.sln` 入参）、多项目/无项目分支、
  TRX 计数与失败明细（含 `StackTrace` 回退、超长截断）、结果渲染与摘要、
  测试优先的移交判定（7 组组合）、步骤许可测试意图矩阵
- 端到端实测：对真实测试项目从零构建 + 运行，`total=13 passed=12`（1 项为并行竞态导致，
  已确认与本工具无关），耗时约 17~20s
- 全量回归 **2071 项通过**；两个语言文件通过 `System.Text.Json` 严格解析

## 四、未完成 / 后续

### 4.1 C++ 测试（主要缺口）

实测项目 `F:\CPP\database`（C++ + CMake(Ninja/cl.exe) + GoogleTest，`gtest_discover_tests` 注册）当前
**只能走 `run_in_terminal`**，因为它没有 .NET 测试项目特征，`run_tests` 会返回 `NO_TESTS:`。

计划做法：

1. 识别：工作区含 `CMakeLists.txt` / `CMakePresets.json`，且出现 `enable_testing()`、
   `include(CTest)` 或 `gtest_discover_tests` / `add_test`
2. 选预设：从 `CMakePresets.json` 读 `configurePresets` / `testPresets`，沿用已配置好的构建目录
3. 执行：`ctest --test-dir <buildDir> --output-on-failure --output-junit <file>`（JUnit 输出 ≡ .NET 侧的 TRX）
4. 收口：终端拦截 `ctest`，删掉文档里「C++ 请用终端」的说明
5. 可选：检测到 `/fsanitize=address` 时把 MSVC ASan 运行时目录注入测试进程 `PATH`
   （实测该项目的 157 个用例全部以 `0xc0000135` STATUS_DLL_NOT_FOUND 失败，正是缺这个）

### 4.2 其他

- 只读 Agent（Ask / Explore）目前不提供 `run_tests`；是否放行留给后续决策（跑测试本身不改文件）
- 覆盖率：VS 安装目录已有 `Microsoft.CodeCoverage.IO.dll` + `datacollector.exe`，
  但产物 vsix 未打包它们，若要出覆盖率需一并处理打包
- 多测试项目场景目前要求显式指定 `project`；后续可考虑按变更文件反查相关测试项目

## 五、分支状态

`feature/run-tests-tool`（基于 `dev` @ `af2acae`），4 个提交，尚未推送：

```
38092a2  feat(tools): 新增 run_tests 工具，.NET 测试不再走终端
a4c4ed6  feat(agent): 改动后优先跑测试，无测试才构建
07a4bac  fix(agent): run_tests 自带构建，并修掉工具服务漏注入
00f2699  feat(agent): 步骤级许可同时覆盖测试，标题含测试意图即放行 run_tests
```

另外两个修复在本分支一并完成（与测试工具无直接依赖，来自实际使用反馈）：

| 提交 | 内容 |
|--|--|
| `f109650` | 选项描述改用正常文本色与字号（issue #109） |
| `cf249e7` | 提问框/审批框/任务面板等适配浅色主题 |
| `fab0b1c` | 自动完成步骤只认正式输出，不再扫思考内容 |
| `af2acae` | 修正 `format` 规则误报，`Get-Date -Format` 不再被拦截 |
