// The Repositories tab: the repositories of an account, filtered here in the window; Clone runs git in the plugin and adds the project.
// The list comes in pages from a stream (`repos.load`); the clones that run are followed with another stream (`clones.watch`).
import { useEffect, useMemo, useRef, useState } from "react";
import { Button, Callout, HTMLSelect, HTMLTable, InputGroup, NonIdealState, Spinner, Switch, Tag } from "@blueprintjs/core";
import { html, useAlta, useRpc, useStream } from "codealta";

const shownRows = 300;

function relative(date) {
    if (!date) return "";
    const days = Math.floor((Date.now() - new Date(date).getTime()) / 86_400_000);
    if (days < 1) return "today";
    if (days < 2) return "yesterday";
    if (days < 60) return `${days} days ago`;
    if (days < 730) return `${Math.floor(days / 30)} months ago`;
    return `${Math.floor(days / 365)} years ago`;
}

export default function Repositories() {
    const alta = useAlta();
    const tab = useRpc("tab.get", {});
    const clones = useStream("clones.watch", {});
    const [accountId, setAccountId] = useState(null);
    const [spaceId, setSpaceId] = useState(null);
    const [repos, setRepos] = useState([]);
    const [loading, setLoading] = useState(false);
    const [loadError, setLoadError] = useState(null);
    const [statuses, setStatuses] = useState({});
    const [query, setQuery] = useState("");
    const [owner, setOwner] = useState("");
    const [hideArchived, setHideArchived] = useState(true);
    const [hideForks, setHideForks] = useState(false);
    const [problem, setProblem] = useState(null);
    const loadRun = useRef(null);
    const finished = useRef(new Set());
    const root = useRef(null);
    const [narrow, setNarrow] = useState(false);

    // A narrow tab shows two columns: the owner, the tags and the date go under the name.
    useEffect(() => {
        const element = root.current;
        if (!element) return undefined;
        const observer = new ResizeObserver(entries => setNarrow(entries[0].contentRect.width < 640));
        observer.observe(element);
        return () => observer.disconnect();
    }, [root.current]);

    const state = tab.data;
    const accounts = state?.accounts ?? [];
    const account = accounts.find(item => item.id === accountId) ?? null;

    useEffect(() => {
        if (!state) return;
        if (!accountId || !accounts.some(item => item.id === accountId)) setAccountId(accounts[0]?.id ?? null);
        if (!spaceId) setSpaceId(state.currentSpaceId ?? state.spaces[0]?.id ?? null);
    }, [state]);

    async function readStatuses(id) {
        try {
            const list = await alta.rpc.invoke("repos.status", { accountId: id }, { signal: alta.closed });
            setStatuses(Object.fromEntries(list.map(item => [item.key, item])));
        } catch (error) {
            if (!alta.closed.aborted) setProblem(error.message);
        }
    }

    async function load(id, refresh) {
        loadRun.current?.abort();
        const run = new AbortController();
        loadRun.current = run;
        const stop = () => run.abort();
        alta.closed.addEventListener("abort", stop);
        setLoading(true);
        setLoadError(null);
        setRepos([]);
        setStatuses({});
        setOwner("");
        const received = [];
        try {
            for await (const item of await alta.rpc.stream("repos.load", { accountId: id, refresh }, { signal: run.signal })) {
                if (item.kind === "error") { setLoadError(item.message); break; }
                if (item.kind === "page") { received.push(...item.repositories); setRepos([...received]); }
                if (item.kind === "done") { await readStatuses(id); break; }
            }
        } catch (error) {
            if (!run.signal.aborted) setLoadError(error.message);
        } finally {
            alta.closed.removeEventListener("abort", stop);
            if (loadRun.current === run) setLoading(false);
        }
    }

    useEffect(() => {
        if (accountId) void load(accountId, false);
        return () => loadRun.current?.abort();
    }, [accountId]);

    // A clone that ends changes where its repository is: read the statuses again once per clone.
    const running = clones.latest ?? [];
    useEffect(() => {
        let ended = false;
        for (const clone of running) {
            const id = `${clone.key}|${clone.state}`;
            if ((clone.state === "done" || clone.state === "failed" || clone.state === "cancelled") && !finished.current.has(id)) {
                finished.current.add(id);
                ended = true;
            }
        }
        if (ended && accountId) void readStatuses(accountId);
    }, [clones.latest]);

    const cloneByKey = useMemo(() => Object.fromEntries(running.map(clone => [clone.key, clone])), [clones.latest]);
    const owners = useMemo(() => [...new Set(repos.map(repo => repo.owner))].sort((a, b) => a.localeCompare(b)), [repos]);
    const filtered = useMemo(() => {
        const text = query.trim().toLowerCase();
        return repos.filter(repo => (!hideArchived || !repo.isArchived) && (!hideForks || !repo.isFork) && (!owner || repo.owner === owner)
            && (!text || repo.fullName.toLowerCase().includes(text) || (repo.description ?? "").toLowerCase().includes(text)));
    }, [repos, query, owner, hideArchived, hideForks]);

    async function clone(repo) {
        setProblem(null);
        try { await alta.rpc.invoke("repos.clone", { accountId: repo.accountId, key: repo.key, spaceId }, { signal: alta.closed }); }
        catch (error) { if (!alta.closed.aborted) setProblem(error.message); }
    }

    async function cancel(repo) {
        try { await alta.rpc.invoke("repos.cancel", { key: repo.key }, { signal: alta.closed }); }
        catch (error) { if (!alta.closed.aborted) setProblem(error.message); }
    }

    if (tab.error) return html`<div style=${{ padding: 16 }}><${Callout} intent="danger" title="The tab could not be read">${tab.error.message}<//></div>`;
    if (!state) return html`<div style=${{ padding: 16 }}><${Spinner} size=${20} /></div>`;
    if (accounts.length === 0) {
        return html`<${NonIdealState} icon="git-repo" title="No account yet"
            description="Add a GitHub or Azure DevOps account in Settings › Repositories: its repositories are listed here."
            action=${html`<${Button} intent="primary" icon="cog" onClick=${() => alta.host.openCanvas("settings")}>Open Settings › Repositories<//>`} />`;
    }

    const statusCell = repo => {
        const job = cloneByKey[repo.key];
        if (job && (job.state === "starting" || job.state === "cloning" || job.state === "adding")) {
            return html`<div style=${{ display: "flex", gap: 6, alignItems: "center" }}>
                <${Spinner} size=${14} />
                <span className="alta-muted" style=${{ maxWidth: 260, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }} title=${job.progress ?? job.message}>${job.progress ?? job.message}</span>
                <${Button} size="small" variant="minimal" icon="cross" title="Cancel the clone" onClick=${() => cancel(repo)}>Cancel<//>
            </div>`;
        }
        const status = statuses[repo.key];
        const failed = job && job.state !== "done"
            ? html`<div style=${{ maxWidth: 360 }}><${Tag} minimal intent=${job.state === "failed" ? "danger" : "none"} style=${{ whiteSpace: "normal" }}>${job.message}<//></div>`
            : null;
        if (!status) return html`<span className="alta-muted">…</span>`;
        if (status.state === "project") return html`<${Tag} minimal intent="success" icon="tick" title=${status.projectPath}>In CodeAlta<//>`;
        if (status.state === "onDisk") return html`<div style=${{ display: "flex", gap: 6, alignItems: "center" }}>
                <${Tag} minimal title=${status.target}>On disk<//>
                <${Button} size="small" icon="add" onClick=${() => clone(repo)}>Add project<//>
            </div>${failed}`;
        if (status.state === "conflict") return html`<${Tag} minimal intent="warning" icon="warning-sign" title=${status.message} style=${{ whiteSpace: "normal" }}>Folder in the way<//>`;
        return html`<${Button} size="small" icon="download" intent="primary" variant="outlined" title=${`Clone into ${status.target}`} onClick=${() => clone(repo)}>Clone<//>${failed}`;
    };

    const rows = filtered.slice(0, shownRows);
    const tags = repo => html`
        ${repo.isPrivate && html`<${Tag} minimal round icon="lock">private<//> `}
        ${repo.isArchived && html`<${Tag} minimal round intent="warning">archived<//> `}
        ${repo.isFork && html`<${Tag} minimal round icon="fork">fork<//>`}`;
    const name = repo => html`
        <a href="#" onClick=${event => { event.preventDefault(); alta.host.openLink(repo.webUrl); }}><strong>${repo.name}</strong></a>
        ${repo.description && html`<div className="alta-muted" style=${{ overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }} title=${repo.description}>${repo.description}</div>`}`;
    return html`
        <div ref=${root} style=${{ padding: 12, display: "flex", flexDirection: "column", gap: 10, height: "100%", boxSizing: "border-box" }}>
            <div style=${{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
                <${HTMLSelect} value=${accountId ?? ""} onChange=${event => setAccountId(event.target.value)} aria-label="Account"
                    options=${accounts.map(item => ({ value: item.id, label: item.displayName }))} />
                <${HTMLSelect} value=${owner} onChange=${event => setOwner(event.target.value)} aria-label="Owner" style=${{ minWidth: 150 }}
                    options=${[{ value: "", label: "Every owner" }, ...owners.map(item => ({ value: item, label: item }))]} />
                <${InputGroup} leftIcon="search" placeholder="Search name or description" value=${query} onChange=${event => setQuery(event.target.value)}
                    style=${{ minWidth: 220 }} />
                <${Switch} checked=${hideArchived} label="Hide archived" style=${{ margin: 0 }} onChange=${event => setHideArchived(event.target.checked)} />
                <${Switch} checked=${hideForks} label="Hide forks" style=${{ margin: 0 }} onChange=${event => setHideForks(event.target.checked)} />
                <span style=${{ flex: 1 }} />
                <${Button} icon="refresh" loading=${loading} disabled=${!accountId} onClick=${() => load(accountId, true)}>Refresh<//>
                <${Button} icon="cog" variant="minimal" title="Accounts" onClick=${() => alta.host.openCanvas("settings")} />
            </div>
            <div className="alta-muted" style=${{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
                <span>Clone into <code>${state.cloneRoot}</code>${state.layout === "owner" ? " / owner" : ""}, add to the space</span>
                <${HTMLSelect} minimal value=${spaceId ?? ""} onChange=${event => setSpaceId(event.target.value)} aria-label="Space"
                    options=${state.spaces.map(space => ({ value: space.id, label: space.name }))} />
                <span>· ${loading ? `${repos.length} read…` : `${filtered.length} of ${repos.length} repositories`}${account ? ` · ${account.host}` : ""}</span>
            </div>
            ${loadError && html`<${Callout} intent="danger" icon="error" title="The repositories could not be listed">${loadError}<//>`}
            ${problem && html`<${Callout} intent="warning" icon="warning-sign">${problem}<//>`}
            <div style=${{ flex: 1, minHeight: 0, overflowY: "auto", overflowX: narrow ? "hidden" : "auto" }}>
                ${rows.length === 0 && !loading && !loadError
                    ? html`<p className="alta-muted">${repos.length === 0 ? "This account lists no repository." : "No repository matches the filter."}</p>`
                    : html`
                    <${HTMLTable} compact striped interactive style=${{ width: "100%", tableLayout: narrow ? "fixed" : "auto" }}>
                        ${narrow
                            ? html`<thead><tr><th>Repository</th><th style=${{ width: 130 }}>Here</th></tr></thead>`
                            : html`<thead><tr><th>Repository</th><th>Owner</th><th></th><th>Updated</th><th>Here</th></tr></thead>`}
                        <tbody>
                            ${rows.map(repo => narrow
                                ? html`
                                <tr key=${repo.key}>
                                    <td style=${{ overflow: "hidden" }}>
                                        ${name(repo)}
                                        <div className="alta-muted" style=${{ display: "flex", gap: 6, flexWrap: "wrap", alignItems: "center", marginTop: 2 }}>
                                            <span>${repo.owner}</span>${tags(repo)}<span title=${repo.updatedAt ?? ""}>${relative(repo.updatedAt)}</span>
                                        </div>
                                    </td>
                                    <td>${statusCell(repo)}</td>
                                </tr>`
                                : html`
                                <tr key=${repo.key}>
                                    <td style=${{ maxWidth: 420 }}>${name(repo)}</td>
                                    <td>${repo.owner}</td>
                                    <td style=${{ whiteSpace: "nowrap" }}>${tags(repo)}</td>
                                    <td style=${{ whiteSpace: "nowrap" }} title=${repo.updatedAt ?? ""}>${relative(repo.updatedAt)}</td>
                                    <td>${statusCell(repo)}</td>
                                </tr>`)}
                        </tbody>
                    <//>`}
                ${filtered.length > shownRows && html`<p className="alta-muted">${shownRows} of ${filtered.length} shown: refine the search to see the others.</p>`}
            </div>
        </div>`;
}
