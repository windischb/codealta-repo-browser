# Repositories (repo-browser)

A source plugin for CodeAlta Desktop. It lists the repositories of your hosting accounts in a **Repositories** tab. **Clone** clones one with `git` into your clone folder and adds it as a project of a space.

Built for CodeAlta 3.4.0. CodeAlta Desktop only; the TUI does not load it.

## What it adds

| Where | What |
| --- | --- |
| Title bar | A **Repositories** button (icon `folder-git-2`) that opens the tab |
| Command palette | `Repositories: open` (`/repos`) |
| Repositories tab | Account picker, owner filter, search, *Hide archived* / *Hide forks*, and one row per repository with its status: **Clone**, **On disk** + **Add project**, **In CodeAlta**, or **Folder in the way** |
| Settings › Extensions › Repositories | The accounts (add, edit, remove, **Check**, **Import from the space**), the **Clone folder** (`clone_root`, default `~/git`) and the **Folder layout** (`flat`: `<clone folder>/<repo>`, `owner`: `<clone folder>/<owner>/<repo>`) |
| Agents | The canvas `plugin:repo-browser/repositories` has the actions `accounts`, `list` and `clone` (see below) |

## Requirements

- `git` on the PATH of CodeAlta. That is the only hard requirement.
- Optional token sources: the GitHub CLI `gh` and the Azure CLI `az`.

## Install

CodeAlta builds and loads every plugin folder in `~/.alta/plugins`. Clone the repository there, under the id of the plugin:

```sh
git clone https://github.com/windischb/codealta-repo-browser ~/.alta/plugins/repo-browser
```

Then click **Build and reload** in Settings › Plugins (or run `alta plugin refresh` in a session). The **Repositories** button appears in the title bar. Read the code first: a plugin runs inside CodeAlta with your rights.

**Update:** `git -C ~/.alta/plugins/repo-browser pull`, then **Build and reload** (`alta plugin reload repo-browser`).

**Remove:** delete `~/.alta/plugins/repo-browser`, then `alta plugin refresh`. The accounts stay in `~/.alta/plugin-data/plugin_repo-browser/` until you delete that folder too.

## Accounts

An account says *where* (service and host), *what* (owners or organizations) and *how to sign in* (a credential **source**). It never holds a token.

| Service | Token from | How it is sent |
| --- | --- | --- |
| GitHub / GitHub Enterprise | **GitHub CLI**: `gh auth token --hostname <host>`, with the profile you give (`GH_CONFIG_DIR=~/.config/gh-work`) | `Bearer` for the API; `Basic x-access-token:<token>` for git |
| | **Environment variable**: its *name*, such as `GITHUB_TOKEN` | same |
| | **No sign-in**: public repositories of the owners you list | nothing |
| Azure DevOps (`dev.azure.com`) | **Azure CLI**: `az account get-access-token` for Azure DevOps, with the profile you give (`AZURE_CONFIG_DIR=…`) | `Bearer` |
| | **Environment variable** holding a personal access token, such as `AZURE_DEVOPS_EXT_PAT` | `Basic :<PAT>` |

- **GitHub owners**: users or organizations to keep. Empty lists every repository the token sees (owned, collaborator, organization member). Without a sign-in, owners are required.
- **Azure DevOps organizations** are required and entered by hand. All projects of each organization are listed; the owner of a repository is `org/project`.
- **Environment variables** are read from the environment CodeAlta was started with. A variable set later needs a restart of CodeAlta.
- **CLI profile**: one `NAME=value` per line, such as `GH_CONFIG_DIR=~/.config/gh-work`. Names that look like secrets (`*TOKEN*`, `*PAT`, `*SECRET*`, `*PASSWORD*`) are refused. While a CLI runs, `GH_TOKEN`/`GITHUB_TOKEN` are removed from its environment, so the profile decides. Note that `gh` falls back to the token of the host in the system keyring when the profile folder holds no sign-in.
- **Check** says who the account signs in as (`Signed in as …`), or why it does not.
- **Import from the space** proposes accounts from the identity of the space the window shows: its `GH_CONFIG_DIR` (and `GH_HOST`) becomes a GitHub account, its `AZURE_CONFIG_DIR` an Azure DevOps account. Review each and enter the Azure DevOps organizations. To import another space, switch to it first.
- **Clone with**: HTTPS (the token of the account) or SSH (your own SSH keys and agent).

## Cloning

- The target is `<clone folder>/<repository>` (or `<clone folder>/<owner>/<repository>`). A name with a path separator, a character Windows refuses in file names, a control character, or a trailing dot or space is refused.
- A repository that is already a project of CodeAlta shows **In CodeAlta** and is never cloned again. Every remote counts, not only `origin`: a clone of an upstream whose `fork` remote is your fork is that fork's project too.
- A target folder with a clone of the same repository (any of its remotes) shows **On disk**; **Add project** only adds it.
- A target folder with a clone of *another* repository of the same name shows **Folder in the way**; the `owner` layout avoids such clashes.
- A target that is a file, holds other files, or is a clone of another repository shows **Folder in the way**: nothing is overwritten.
- `git clone --progress` runs without a shell. The progress is shown in the row. **Cancel** stops git and removes the folder the clone created.
- Then `alta project add <folder> --space <space>` adds the project to the space chosen above the table (the space the window shows by default).
- A clone runs in the plugin, not in the tab: closing or hiding the tab does not stop it.

## What is kept, and what never is

| Kept | Where |
| --- | --- |
| The accounts (no token) | `~/.alta/plugin-data/plugin_repo-browser/accounts.json` |
| Clone folder and layout | `~/.alta/config.toml`, `[plugins.repo-browser]` |
| The repository lists | In memory only; **Refresh** reads them again |
| Tokens | In memory only, for at most 5 minutes |

- A token is never written to disk, never logged and never part of a message.
- A token is sent only to the host of its account. A paging link to another host is not followed.
- For a clone, the token reaches git only through the environment of that one git process, as a header scoped to the host (`GIT_CONFIG_COUNT`/`GIT_CONFIG_KEY_0=http.https://<host>/.extraHeader`). It is not in the remote URL, in the arguments or in `.git/config`. `GIT_TERMINAL_PROMPT=0` makes a refused sign-in fail instead of waiting for input.
- Processes of the same user can read the environment of that git process while it runs, as they can read what `gh` and `az` print.

## For agents

```text
alta canvas show plugin:repo-browser/repositories           # actions, schemas, and what is loaded
alta canvas invoke plugin:repo-browser/repositories accounts
alta canvas invoke plugin:repo-browser/repositories list --stdin    {"account": "github-me", "query": "api", "limit": 20}
alta canvas invoke plugin:repo-browser/repositories clone --stdin   {"account": "github-me", "repository": "owner/name", "space": "work"}
```

`list` returns each repository with `status` (`project`, `onDisk`, `conflict`, `none`) and its path. `clone` waits for the end and returns `state` (`done`, `failed`, `cancelled`), the path and a message. Errors come back as `{ "error": "<code>", "message": "…" }`.

## Files

```text
plugin.cs                # plugin class: settings, button, command, canvases, RPC, actions
Accounts.cs              # account model, validation, store, credential resolver, CLI and process helpers
Providers/Hosting.cs     # shared HTTP client logic, repository keys
Providers/GitHub.cs      # GitHub and GitHub Enterprise
Providers/AzureDevOps.cs # Azure DevOps Services
Cloning.cs               # project index, target folders, git clone, progress, cancel
ui/settings.js           # Settings › Repositories
ui/repositories.js       # the Repositories tab
```

**Developing it.** Work in a clone of your own (such as `~/git/codealta-repo-browser`) and keep the installed clone in `~/.alta/plugins/repo-browser` with that folder as a remote: commit in the first, then `git -C ~/.alta/plugins/repo-browser pull` and `alta plugin reload repo-browser`. A link from `~/.alta/plugins` to your clone does not build in CodeAlta 3.4.0: the build runs in the folder the link resolves to, which does not see the `Directory.Build.props` CodeAlta writes in `~/.alta/plugins`.

## Not yet

GitLab and Bitbucket (the model has room for them), forking or creating repositories, and the TUI.
