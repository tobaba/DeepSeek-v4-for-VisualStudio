namespace DeepSeek_v4_for_VisualStudio.CodeCompletion
{
    /// <summary>
    /// 自动补全支持的内容类型定义。
    ///
    /// ── 为什么需要这个文件 ──
    /// <see cref="GhostTextTaggerProvider"/> 与 <see cref="TextViewCreationListener"/> 原先只声明
    /// <c>[ContentType("code")]</c>。<c>code</c> 是 VS 中"可编程语言"的抽象基类型，
    /// C#/VB/F#/Python 等继承自它，因此这些语言本就可用；但：
    ///   • <c>C/C++</c> 使用独立内容类型，**不继承 code**；
    ///   • JSON/XML/YAML 等配置文件同样不在 <c>code</c> 之下。
    /// 在这些文件里补全完全不触发（MEF 根本不会调用该导出）。
    ///
    /// 注意：Markdown、纯文本等**文档类**内容类型刻意不在支持范围内——
    /// 幽灵文本补全面向代码与结构化配置，对散文类文本既无价值也易造成干扰。
    ///
    /// ── 实现方式说明 ──
    /// VS 的内容类型是**单继承**的，且上列类型由 VS/第三方语言服务注册，
    /// 扩展无法把自己定义的聚合类型插进它们的继承链（无法让 Json "同时继承"
    /// code 与自定义类型）。因此正确做法是在**导出声明上并列多个 [ContentType]**——
    /// MEF 允许同一导出标注多个内容类型，任一匹配即生效。
    ///
    /// 本文件集中维护这份清单，避免清单散落在两个导出上导致漏改。
    /// </summary>
    internal static class DeepSeekCompletionContentTypes
    {
        // ── 编程语言 ──

        /// <summary>C/C++（独立内容类型，不继承 code —— 关键修复项）。</summary>
        internal const string Cpp = "C/C++";

        /// <summary>Java。</summary>
        internal const string Java = "java";

        /// <summary>SQL。</summary>
        internal const string Sql = "sql";

        /// <summary>CMake（CMakeLists.txt 等）。</summary>
        internal const string Cmake = "cmake";

        // ── 配置与数据 ──

        /// <summary>JSON（含 appsettings.json 等）。</summary>
        internal const string Json = "json";

        /// <summary>XML。</summary>
        internal const string Xml = "xml";

        /// <summary>YAML。</summary>
        internal const string Yaml = "yaml";

        /// <summary>INI / 通用配置文件（.editorconfig、.gitconfig 等）。</summary>
        internal const string Ini = "ini";

        // ── 标记与样式 ──

        /// <summary>HTML。</summary>
        internal const string Html = "html";

        /// <summary>CSS。</summary>
        internal const string Css = "css";

        /// <summary>Razor。</summary>
        internal const string Razor = "razor";

        /// <summary>
        /// 需要**显式声明**的内容类型名称（供日志/诊断与单元测试使用）。
        ///
        /// 不含 C#/VB/F#/Python 等继承自 <c>code</c> 的语言——它们由导出上已有的
        /// <c>[ContentType("code")]</c> 覆盖，无需重复登记。
        /// 也不含 Markdown/纯文本等文档类型（刻意不支持）。
        ///
        /// 注意：此数组**不驱动** MEF 导出——导出上的 [ContentType] 必须逐个手写。
        /// </summary>
        internal static readonly string[] Names =
        {
            Cpp, Java, Sql, Cmake,
            Json, Xml, Yaml, Ini,
            Html, Css, Razor,
        };
    }
}