# Domain Docs

Engineering skills 探索 codebase 时，应如何消费这个 repo 的 domain documentation。

## Before exploring, read these

- repo 根目录的 **`CONTEXT.md`**。
- **`docs/adr/`** 中与即将处理区域相关的 ADRs。

如果这些文件不存在，静默继续，不要把缺失本身视为问题。`domain-modeling` skill 会在术语或决策实际得到明确时按需创建它们。

## File structure

RemoteCI 使用 single-context 布局：

下图只表示 domain documentation 的位置，不是完整的源码目录树：

```
/
├── CONTEXT.md
└── docs/
    └── adr/
```

## Use the glossary's vocabulary

当输出命名某个 domain concept 时，包括 issue title、refactor proposal、hypothesis 或 test name，应使用 `CONTEXT.md` 中定义的术语，不要改用 glossary 明确避免的同义词。

如果所需概念尚未出现在 glossary 中，应先判断自己是否正在发明项目没有使用的语言；如果确有缺口，则交给 `domain-modeling` 记录。

## Flag ADR conflicts

如果输出与现有 ADR 矛盾，应明确指出冲突，而不是静默覆盖。
