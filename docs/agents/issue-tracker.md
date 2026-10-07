# Issue tracker: GitHub

RemoteCI 的 issues 和 PRDs 存放在 `Edge-HH/RemoteCI` 的 GitHub Issues 中，使用 `gh` CLI 操作。

## Target guardrail

从本仓库 clone 内运行命令。第一次写入前，运行以下命令；只有输出严格等于 `Edge-HH/RemoteCI` 时才继续：

```powershell
gh repo view --json nameWithOwner --jq .nameWithOwner
```

如果不在本仓库目录中，为每条命令显式添加 `--repo Edge-HH/RemoteCI`。

## Conventions

- **Create an issue**: `gh issue create --title "..." --body-file -`，从标准输入读取多行 body。
- **Read an issue**: `gh issue view <number> --json number,title,body,labels,comments --jq '{number, title, body, labels: [.labels[].name], comments: [.comments[].body]}'`。
- **List issues**: `gh issue list --state open --limit 1000 --json number,title,body,labels,comments --jq '[.[] | {number, title, body, labels: [.labels[].name], comments: [.comments[].body]}]'`，按需添加 `--label` 或调整 `--state`。
- **Comment on an issue**: `gh issue comment <number> --body-file -`，从标准输入读取多行 comment。
- **Apply / remove labels**: `gh issue edit <number> --add-label "..."` / `--remove-label "..."`
- **Close**: `gh issue close <number> --comment "..."`

## Pull requests as a triage surface

**PRs as a request surface: no.** _（如果这个 repo 把 external PRs 当作 feature requests，则设为 `yes`；`/triage` 会读取这个 flag。）_

设为 `yes` 时，PRs 走与 issues 相同的 labels 和 states：

- **Read a PR**: `gh pr view <number> --json number,title,body,labels,comments,author`，以及 `gh pr diff <number>` 获取 diff。
- **List external PRs for triage**: 使用 REST response 中真实存在的 `author_association` 字段：

  ```powershell
  gh api --paginate 'repos/Edge-HH/RemoteCI/pulls?state=open&per_page=100' --jq '.[] | select(.author_association != "OWNER" and .author_association != "MEMBER" and .author_association != "COLLABORATOR") | {number, title, author: .user.login, authorAssociation: .author_association, labels: [.labels[].name]}'
  ```

- **Comment / label / close**: `gh pr comment`、`gh pr edit --add-label`/`--remove-label`、`gh pr close`。

GitHub 在 issues 和 PRs 之间共享一个 number space，因此裸 `#42` 可能是两者之一——用 `gh pr view 42` 解析，失败则回退到 `gh issue view 42`。

## When a skill says "publish to the issue tracker"

创建一个 GitHub issue。

## When a skill says "fetch the relevant ticket"

运行 `gh issue view <number> --comments`。

## Wayfinding operations

供 `/wayfinder` 使用。**map** 是单个 issue，以 **child** issues 作为 tickets。

- **Labels**: map 使用 `wayfinder:map`；child ticket 按类型使用 `wayfinder:research`、`wayfinder:prototype`、`wayfinder:grilling` 或 `wayfinder:task`。
- **Map**: `gh issue create --label wayfinder:map --title "..." --body-file -`。
- **Child ticket**: `gh issue create --parent <map-number> --label wayfinder:<type> --title "..." --body-file -`。GitHub sub-issue 是 canonical parent/child 关系。
- **Blocking**: `gh issue edit <child-number> --add-blocked-by <blocker-number>`。已知 blocker 时也可在创建 child 时使用 `--blocked-by <blocker-number>`。
- **Frontier query**: 先用 `gh issue view <map-number> --json subIssues --jq '.subIssues[] | select(.state == "OPEN") | .number'` 获取 open children；再对每个 child 运行 `gh issue view <child-number> --json number,title,state,assignees,blockedBy`，只保留无 assignee 且没有 open blocker 的 issue，并按 map 中的顺序选择第一个。
- **Claim**: `gh issue edit <n> --add-assignee "@me"`。
- **Resolve**: 用 comment 记录答案并关闭 child，再向 map 的 Decisions-so-far 追加 ticket 名称、链接和一行结论。
