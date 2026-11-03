// The Repositories tab: the repositories of an account, filtered here in the window; Clone runs git in the plugin and adds the project.
// The list comes in pages from a stream (`repos.load`); the clones that run are followed with another stream (`clones.watch`).
import { useEffect, useMemo, useRef, useState } from "react";
import { Button, Callout, Card, Classes, Colors, HTMLSelect, InputGroup, NonIdealState, ProgressBar, Spinner, Switch, Tag } from "@blueprintjs/core";
import { BrandIcon, Icon, html, isBrandIcon, useAlta, useRpc, useStream, useTheme } from "codealta";

const shownRows = 300;
const skeletonRows = 8;
// The window has no Azure DevOps mark yet: `isBrandIcon` keeps the Lucide fallback until it has one.
const providerBrands = { gitHub: "github", azureDevOps: "azuredevops" };
const runningLabels = { starting: "Starting", cloning: "Cloning", adding: "Adding" };

// Every selector starts with `.rb-root`: the element is in the document, the rules stay in this tab.
const css = `
.rb-root { display: flex; flex-direction: column; gap: 12px; min-width: 0; box-sizing: border-box; }
.rb-root .rb-head { position: sticky; top: 0; z-index: 3; display: flex; flex-direction: column; gap: 10px; padding: 12px 16px; transition: box-shadow 120ms ease; }
.rb-root .rb-line { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; min-width: 0; }
.rb-root .rb-spacer { flex: 1 1 0; }
.rb-root.rb-narrow .rb-head { position: static; }
.rb-root .rb-buttons { display: inline-flex; align-items: center; gap: 4px; margin-left: auto; }
.rb-root .rb-search { flex: 1 1 240px; min-width: 0; }
.rb-root .rb-switch { margin: 0; white-space: nowrap; }
.rb-root .rb-muted { color: var(--rb-muted); }
.rb-root .rb-small { font-size: 12px; }
.rb-root .rb-inline { display: inline-flex; align-items: center; gap: 6px; min-width: 0; }
.rb-root .rb-target { flex-wrap: wrap; }
.rb-root .rb-target code { overflow-wrap: anywhere; }
.rb-root .rb-account { font-weight: 600; }
.rb-root .${Classes.HTML_SELECT}.${Classes.MINIMAL} select { padding-right: 26px; }
.rb-root .rb-head.rb-stuck { border-top-left-radius: 0; border-top-right-radius: 0; }
.rb-root .rb-list { padding: 0; overflow: hidden; }
.rb-root .rb-row { display: grid; grid-template-columns: 30px minmax(0, 1fr) 120px 240px; align-items: center; column-gap: 12px; padding: 8px 16px; border-top: 1px solid var(--rb-line); }
.rb-root .rb-row:first-child { border-top: 0; }
.rb-root .rb-row:hover { background: var(--rb-hover); }
.rb-root.rb-narrow .rb-row { grid-template-columns: 28px minmax(0, 1fr) auto; padding: 8px 12px; }
.rb-root .rb-glyph { width: 30px; height: 30px; border-radius: 8px; display: grid; place-items: center; background: var(--rb-hover); color: var(--rb-muted); }
.rb-root.rb-narrow .rb-glyph { width: 28px; height: 28px; }
.rb-root .rb-main { min-width: 0; }
.rb-root .rb-title { display: flex; align-items: center; gap: 6px; min-width: 0; line-height: 20px; }
.rb-root .rb-owner { color: var(--rb-muted); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; flex: 0 3 auto; min-width: 2ch; }
.rb-root .rb-name { font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; flex: 0 1 auto; min-width: 0; }
.rb-root .rb-title .${Classes.TAG} { flex: none; }
.rb-root .rb-desc { color: var(--rb-muted); font-size: 12px; line-height: 18px; margin-top: 2px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.rb-root .rb-date { color: var(--rb-muted); font-size: 12px; white-space: nowrap; }
.rb-root .rb-row > .rb-date { text-align: right; }
.rb-root .rb-meta { display: flex; gap: 6px; align-items: center; margin-top: 4px; }
.rb-root .rb-status { display: flex; flex-direction: column; align-items: flex-end; gap: 4px; min-width: 0; }
.rb-root.rb-narrow .rb-status { max-width: 180px; }
.rb-root .rb-state { display: inline-flex; align-items: center; gap: 6px; font-size: 12px; white-space: nowrap; max-width: 100%; }
.rb-root .rb-actions { display: inline-flex; align-items: center; gap: 8px; max-width: 100%; }
.rb-root .rb-note { font-size: 12px; line-height: 16px; text-align: right; max-width: 100%; overflow: hidden; display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; overflow-wrap: anywhere; }
.rb-root .rb-note.rb-one { display: block; white-space: nowrap; text-overflow: ellipsis; }
.rb-root .rb-progress { width: 100%; max-width: 220px; }
.rb-root .rb-empty { padding: 40px 16px; }
.rb-root .rb-more { text-align: center; padding: 4px 0 8px; }
`;

function relative(date) {
    if (!date) return "";
    const days = Math.floor((Date.now() - new Date(date).getTime()) / 86_400_000);
    if (days < 1) return "today";
    if (days < 2) return "yesterday";
    if (days < 60) return `${days} days ago`;
    if (days < 730) return `${Math.floor(days / 30)} months ago`;
    return `${Math.floor(days / 365)} years ago`;
}

function exactDate(date) {
    if (!date) return "";
    const value = new Date(date);
    return Number.isNaN(value.getTime()) ? date : `Updated ${value.toLocaleString()}`;
}

// git writes "Receiving objects:  45% (450/1000)": the last percentage of the line is the progress.
function percent(text) {
    const found = [...(text ?? "").matchAll(/(\d{1,3})%/g)].pop();
    return found ? Math.min(100, Number(found[1])) : null;
}

function ProviderMark({ provider, size }) {
    const brand = providerBrands[provider];
    return brand && isBrandIcon(brand)
        ? html`<${BrandIcon} name=${brand} size=${size} />`
        : html`<${Icon} name="git-branch" size=${size} />`;
}

export default function Repositories() {
    const alta = useAlta();
    const theme = useTheme();
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
    const [hidePrivate, setHidePrivate] = useState(false);
    const [problem, setProblem] = useState(null);
    const loadRun = useRef(null);
    const finished = useRef(new Set());
    const root = useRef(null);
    const sentinel = useRef(null);
    const [narrow, setNarrow] = useState(false);
    const [stuck, setStuck] = useState(false);

    // A narrow tab shows two columns: the date goes under the name.
    useEffect(() => {
        const element = root.current;
        if (!element) return undefined;
        const observer = new ResizeObserver(entries => setNarrow(entries[0].contentRect.width < 640));
        observer.observe(element);
        return () => observer.disconnect();
    }, [root.current]);

    // The toolbar casts a shadow once the list scrolls under it: the empty element above it has left the view.
    useEffect(() => {
        const element = sentinel.current;
        if (!element) return undefined;
        const observer = new IntersectionObserver(entries => setStuck(!entries[0].isIntersecting));
        observer.observe(element);
        return () => observer.disconnect();
    }, [sentinel.current]);

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
    // An account without a sign-in lists public repositories only: there is nothing private to hide, and the switch is disabled.
    const signedIn = (state?.accounts ?? []).find(item => item.id === accountId)?.credential !== "none";
    const hidingPrivate = hidePrivate && signedIn;
    const filtered = useMemo(() => {
        const text = query.trim().toLowerCase();
        return repos.filter(repo => (!hideArchived || !repo.isArchived) && (!hideForks || !repo.isFork) && (!hidingPrivate || !repo.isPrivate)
            && (!owner || repo.owner === owner)
            && (!text || repo.fullName.toLowerCase().includes(text) || (repo.description ?? "").toLowerCase().includes(text)));
    }, [repos, query, owner, hideArchived, hideForks, hidingPrivate]);

    async function clone(repo) {
        setProblem(null);
        try { await alta.rpc.invoke("repos.clone", { accountId: repo.accountId, key: repo.key, spaceId }, { signal: alta.closed }); }
        catch (error) { if (!alta.closed.aborted) setProblem(error.message); }
    }

    async function cancel(repo) {
        try { await alta.rpc.invoke("repos.cancel", { key: repo.key }, { signal: alta.closed }); }
        catch (error) { if (!alta.closed.aborted) setProblem(error.message); }
    }

    function clearFilters() {
        setQuery("");
        setOwner("");
        setHideArchived(false);
        setHidePrivate(false);
        setHideForks(false);
    }

    if (tab.error) return html`<div style=${{ padding: 16 }}><${Callout} intent="danger" title="The tab could not be read">${tab.error.message}<//></div>`;
    if (!state) return html`<div style=${{ padding: 48, display: "flex", justifyContent: "center" }}><${Spinner} size=${24} /></div>`;
    if (accounts.length === 0) {
        return html`<div style=${{ paddingTop: 48 }}><${NonIdealState} icon=${html`<${Icon} name="folder-git-2" size=${48} />`} title="No account yet"
            description="Add a GitHub or Azure DevOps account in Settings › Repositories: its repositories are listed here."
            action=${html`<${Button} intent="primary" icon=${html`<${Icon} name="settings" />`} onClick=${() => alta.host.openCanvas("settings")}>Open Settings › Repositories<//>`} /></div>`;
    }

    // The colors of the intents, as Blueprint writes intent text in each theme.
    const tones = theme.dark
        ? { success: Colors.GREEN5, warning: Colors.ORANGE5, danger: Colors.RED5 }
        : { success: Colors.GREEN2, warning: Colors.ORANGE2, danger: Colors.RED2 };
    const vars = {
        "--rb-muted": theme.muted,
        "--rb-line": theme.dark ? "rgba(255, 255, 255, 0.08)" : "rgba(17, 20, 24, 0.08)",
        "--rb-hover": "rgba(143, 153, 168, 0.12)",
    };
    const lucide = (name, size = 14) => html`<${Icon} name=${name} size=${size} />`;

    const statusCell = repo => {
        const job = cloneByKey[repo.key];
        if (job && runningLabels[job.state]) {
            const text = job.progress ?? job.message ?? "";
            const value = percent(job.progress);
            return html`<div className="rb-status">
                <div className="rb-actions">
                    <span className="rb-state"><${Spinner} size=${14} />${runningLabels[job.state]}${value !== null ? ` ${value}%` : "…"}</span>
                    <${Button} size="small" variant="minimal" icon=${lucide("x")} title="Cancel the clone" onClick=${() => cancel(repo)}>Cancel<//>
                </div>
                <div className="rb-progress"><${ProgressBar} intent="primary" value=${value !== null ? value / 100 : undefined} stripes=${value === null} animate=${value === null} /></div>
                ${text && html`<div className="rb-note rb-one rb-muted" title=${text}>${text}</div>`}
            </div>`;
        }
        const status = statuses[repo.key];
        const failed = job && job.state !== "done" && job.message
            ? html`<div className="rb-note" style=${{ color: job.state === "failed" ? tones.danger : theme.muted }} title=${job.message}>${job.message}</div>`
            : null;
        if (!status) return html`<div className="rb-status"><span className=${Classes.SKELETON} style=${{ width: 72, height: 24, borderRadius: 4 }} /></div>`;
        if (status.state === "project") {
            return html`<div className="rb-status"><span className="rb-state" style=${{ color: tones.success }} title=${status.projectPath ?? ""}>
                ${lucide("circle-check")}In CodeAlta</span></div>`;
        }
        if (status.state === "conflict") {
            return html`<div className="rb-status"><span className="rb-state" style=${{ color: tones.warning }} title=${status.message ?? ""}>
                ${lucide("triangle-alert")}Folder in the way</span></div>`;
        }
        if (status.state === "onDisk") {
            return html`<div className="rb-status">
                <div className="rb-actions">
                    ${!narrow && html`<span className="rb-state rb-muted" title=${status.target}>${lucide("hard-drive")}On disk</span>`}
                    <${Button} size="small" icon=${lucide("folder-plus")} title=${`${status.target} is on disk: add it to the space`} onClick=${() => clone(repo)}>Add project<//>
                </div>${failed}
            </div>`;
        }
        return html`<div className="rb-status">
            <${Button} size="small" intent="primary" variant="outlined" icon=${lucide("download")} title=${`Clone into ${status.target}`} onClick=${() => clone(repo)}>Clone<//>${failed}
        </div>`;
    };

    const glyph = repo => html`<div className="rb-glyph" title=${repo.isArchived ? "Archived" : repo.isFork ? "Fork" : "Repository"}>
        ${lucide(repo.isArchived ? "archive" : repo.isFork ? "git-fork" : "book-marked", 16)}</div>`;
    const date = repo => html`<span className="rb-date" title=${exactDate(repo.updatedAt)}>${relative(repo.updatedAt)}</span>`;
    const row = repo => html`
        <div className="rb-row" key=${repo.key}>
            ${glyph(repo)}
            <div className="rb-main">
                <div className="rb-title">
                    <span className="rb-owner" title=${repo.owner}>${repo.owner} /</span>
                    <a className="rb-name" href="#" title=${`Open ${repo.fullName} in the browser`}
                        onClick=${event => { event.preventDefault(); alta.host.openLink(repo.webUrl); }}>${repo.name}</a>
                    ${repo.isPrivate && html`<${Tag} minimal round icon=${lucide("lock", 11)} title="Private">Private<//>`}
                    ${repo.isFork && html`<${Tag} minimal round>Fork<//>`}
                    ${repo.isArchived && html`<${Tag} minimal round intent="warning">Archived<//>`}
                </div>
                ${repo.description && html`<div className="rb-desc" title=${repo.description}>${repo.description}</div>`}
                ${narrow && html`<div className="rb-meta">${date(repo)}</div>`}
            </div>
            ${!narrow && date(repo)}
            ${statusCell(repo)}
        </div>`;
    const skeletons = Array.from({ length: skeletonRows }, (_, index) => html`
        <div className="rb-row" key=${`skeleton-${index}`} aria-hidden="true">
            <div className=${`rb-glyph ${Classes.SKELETON}`} />
            <div className="rb-main">
                <div className=${Classes.SKELETON} style=${{ width: `${28 + (index * 17) % 36}%`, height: 14, margin: "3px 0" }} />
                <div className=${Classes.SKELETON} style=${{ width: `${45 + (index * 23) % 40}%`, height: 12, marginTop: 6 }} />
            </div>
            ${!narrow && html`<div className=${Classes.SKELETON} style=${{ width: 80, height: 12, justifySelf: "end" }} />`}
            <div className="rb-status"><span className=${Classes.SKELETON} style=${{ width: 72, height: 24 }} /></div>
        </div>`);

    const rows = filtered.slice(0, shownRows);
    const filtering = query.trim() !== "" || owner !== "" || hideArchived || hideForks || hidingPrivate;
    let list;
    if (loadError && repos.length === 0) list = null;
    else if (rows.length > 0) list = html`<${Card} className="rb-list">${rows.map(row)}<//>`;
    else if (loading) list = html`<${Card} className="rb-list">${skeletons}<//>`;
    else if (repos.length === 0) {
        list = html`<${Card} className="rb-empty"><${NonIdealState} icon=${html`<${Icon} name="folder-git-2" size=${40} />`}
            title="No repository" description="This account lists no repository." /><//>`;
    } else {
        list = html`<${Card} className="rb-empty"><${NonIdealState} icon=${html`<${Icon} name="search-x" size=${40} />`}
            title="No repository matches the filter"
            description=${`None of the ${repos.length} repositories of this account matches the search and the filters.`}
            action=${filtering && html`<${Button} icon=${lucide("filter-x")} onClick=${clearFilters}>Clear the search and the filters<//>`} /><//>`;
    }

    return html`
        <div ref=${root} className=${`rb-root${narrow ? " rb-narrow" : ""}`} style=${vars}>
            <style>${css}</style>
            <div ref=${sentinel} style=${{ height: 1, marginBottom: -13 }} />
            <${Card} className=${`rb-head${stuck ? " rb-stuck" : ""}`} elevation=${stuck ? 2 : 0}>
                <div className="rb-line">
                    <span className="rb-inline">
                        ${account && html`<${ProviderMark} provider=${account.provider} size=${18} />`}
                        <${HTMLSelect} className="rb-account" minimal value=${accountId ?? ""} onChange=${event => setAccountId(event.target.value)} aria-label="Account"
                            options=${accounts.map(item => ({ value: item.id, label: item.displayName }))} />
                    </span>
                    ${account && html`<span className="rb-muted rb-small">${account.host}</span>`}
                    <span className="rb-spacer" />
                    <span className="rb-inline rb-target rb-muted rb-small">
                        ${lucide("folder-down", 13)}
                        <span>Clone into <code>${state.cloneRoot}</code>${state.layout === "owner" ? " / owner" : ""}, add to the space</span>
                        <${HTMLSelect} minimal value=${spaceId ?? ""} onChange=${event => setSpaceId(event.target.value)} aria-label="Space"
                            options=${state.spaces.map(space => ({ value: space.id, label: space.name }))} />
                    </span>
                    <span className="rb-buttons">
                        <${Button} icon=${lucide("refresh-cw")} loading=${loading} disabled=${!accountId} onClick=${() => load(accountId, true)}>Refresh<//>
                        <${Button} icon=${lucide("settings")} variant="minimal" title="Accounts" aria-label="Accounts" onClick=${() => alta.host.openCanvas("settings")} />
                    </span>
                </div>
                <div className="rb-line">
                    <${InputGroup} className="rb-search" leftIcon="search" placeholder="Search name or description" value=${query} onChange=${event => setQuery(event.target.value)}
                        rightElement=${query && html`<${Button} variant="minimal" icon=${lucide("x")} title="Clear the search" aria-label="Clear the search" onClick=${() => setQuery("")} />`} />
                    <${HTMLSelect} value=${owner} onChange=${event => setOwner(event.target.value)} aria-label="Owner" style=${{ minWidth: 150 }}
                        options=${[{ value: "", label: "Every owner" }, ...owners.map(item => ({ value: item, label: item }))]} />
                    <${Switch} className="rb-switch" checked=${hideArchived} label="Hide archived" onChange=${event => setHideArchived(event.target.checked)} />
                    <${Switch} className="rb-switch" checked=${hideForks} label="Hide forks" onChange=${event => setHideForks(event.target.checked)} />
                    <span title=${signedIn ? "" : "No sign-in: this account lists public repositories only."}>
                        <${Switch} className="rb-switch" checked=${hidingPrivate} disabled=${!signedIn} label="Hide private"
                            onChange=${event => setHidePrivate(event.target.checked)} />
                    </span>
                    <span className="rb-spacer" />
                    <span className="rb-inline rb-muted rb-small" aria-live="polite">
                        ${loading
                            ? html`<${Spinner} size=${12} />${repos.length} read…`
                            : html`<strong>${filtered.length}</strong> of ${repos.length} repositories`}
                    </span>
                </div>
            <//>
            ${loadError && html`<${Callout} intent="danger" icon="error" title="The repositories could not be listed">${loadError}<//>`}
            ${problem && html`<${Callout} intent="warning" icon="warning-sign">${problem}<//>`}
            ${list}
            ${filtered.length > shownRows && html`<div className="rb-more rb-muted rb-small">${shownRows} of ${filtered.length} shown: refine the search to see the others.</div>`}
        </div>`;
}
