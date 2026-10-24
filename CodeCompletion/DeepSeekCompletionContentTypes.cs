using Microsoft.VisualStudio.Utilities;
using System.ComponentModel.Composition;

namespace DeepSeek_v4_for_VisualStudio.CodeCompletion
{
    /// <summary>
    /// 自动补全支持的内容类型定义。
    ///
    /// ── 为什么需要这个文件 ──
    /// <see cref="GhostTextTaggerProvider"/> 与 <see cref="TextViewCreationListener"/> 原先只声明
    /// <c>[ContentType("code")]</c>。<c>code</c> 是 VS 中"可编程语言"的抽象基类型，
    /// C#/VB/F#/Python 等继承自它，但：
    ///   • <c>C/C++</c> 使用独立内容类型，**不继承 code**；
    ///   • JSON/XML/YAML/Markdown 等配置与文档类型同样不在 <c>code</c> 之下。
    /// 因此在这些文件里补全完全不触发（MEF 根本不会调用该导出）。
    ///
    /// ── 实现方式说明 ──
    /// VS 的内容类型是**单继承**的，且上列类型由 VS/第三方语言服务注册，
    /// 扩展无法把自己定义的聚合类型插进它们的继承链（无法让 Json "同时继承"
    /// code 与自定义类型）。因此正确做法是在**导出声明上并列多个 [ContentType]**——
    /// MEF 允许同一导出标注多个内容类型，任一匹配即生效。
    ///
    /// 本文件集中维护这份清单，避免清单散落在两个导出上导致漏改。
    /// 新增语言时：在 <see cref="Names"/> 中登记，并同步为两个导出的
    /// <c>[ContentType]</c> 特性各加一行（特性参数必须是编译期常量，无法用数组展开）。
    /// </summary>
    internal static class DeepSeekCompletionContentTypes
    {
        // ── 编程语言 ──
        /// <summary>C/C++（独立内容类型，不继承 code —— 关键修复项）。</summary>
        internal const string Cpp = "C/C++";

        /// <summary>C#。</summary>
        internal const string CSharp = "csharp";

        /// <summary>Visual Basic。</summary>
        internal const string VisualBasic = "vb";

        /// <summary>F#。</summary>
        internal const string FSharp = "f#";

        /// <summary>Python。</summary>
        internal const string Python = "python";

        /// <summary>JavaScript。</summary>
        internal const string JavaScript = "javascript";

        /// <summary>TypeScript。</summary>
        internal const string TypeScript = "typescript";

        /// <summary>Java。</summary>
        internal const string Java = "java";

        /// <summary>SQL。</summary>
        internal const string Sql = "sql";

        /// <summary>CMake。</summary>
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

        /// <summary>Markdown。</summary>
        internal const string Markdown = "markdown";

        // ── 通用文本 ──
        /// <summary>纯文本（.txt、.gitignore、无扩展名文件等）。</summary>
        internal const string PlainText = "plaintext";

        /// <summary>
        /// 全部受支持的内容类型名称（供日志/诊断与单元测试使用）。
        /// 注意：此数组**不驱动** MEF 导出——导出上的 [ContentType] 必须逐个手写。
        /// </summary>
        internal static readonly string[] Names =
        {
            Cpp, CSharp, VisualBasic, FSharp, Python, JavaScript, TypeScript,
            Java, Sql, Cmake,
            Json, Xml, Yaml, Ini,
            Html, Css, Razor, Markdown,
            PlainText,
        };
    }
}