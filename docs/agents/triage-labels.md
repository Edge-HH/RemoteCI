# Triage Labels

`triage` 使用两个 category roles 和五个 state roles。这个文件把它们映射到 RemoteCI GitHub Issues 中的实际 label 字符串。

应用 label 前，运行 `gh label list --limit 200 --json name --jq '.[].name'` 确认目标字符串存在；缺失时报告配置错误，不要静默替换成近似名称。

## Category labels

| Role          | Label in our tracker | Meaning             |
| ------------- | -------------------- | ------------------- |
| `bug`         | `bug`                | Something is broken |
| `enhancement` | `enhancement`        | Feature or improvement request |

## State labels

| Label in mattpocock/skills | Label in our tracker | Meaning                                  |
| -------------------------- | -------------------- | ---------------------------------------- |
| `needs-triage`             | `needs-triage`       | Maintainer needs to evaluate this issue  |
| `needs-info`               | `needs-info`         | Waiting on reporter for more information |
| `ready-for-agent`          | `ready-for-agent`    | Fully specified, ready for an AFK agent  |
| `ready-for-human`          | `ready-for-human`    | Requires human implementation            |
| `wontfix`                  | `wontfix`            | Will not be actioned                     |

当某个 skill 提到 category 或 state role 时，使用对应表格中的 label 字符串。
