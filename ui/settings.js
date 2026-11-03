// Settings › Repositories: the hosting accounts the Repositories tab lists. An account names where its token comes from
// (an environment variable, a CLI profile), never a token: the plugin reads it when it needs it and keeps it in memory only.
import { useEffect, useId, useState } from "react";
import { Button, Callout, Card, FormGroup, HTMLSelect, InputGroup, Tag, TextArea } from "@blueprintjs/core";
import { html, useAlta, useRpc } from "codealta";

const providers = [
    { value: "gitHub", label: "GitHub / GitHub Enterprise", host: "github.com", cli: "GH_CONFIG_DIR", cliName: "GitHub CLI (gh)", variable: "GITHUB_TOKEN" },
    { value: "azureDevOps", label: "Azure DevOps", host: "dev.azure.com", cli: "AZURE_CONFIG_DIR", cliName: "Azure CLI (az)", variable: "AZURE_DEVOPS_EXT_PAT" },
];

const providerOf = value => providers.find(item => item.value === value) ?? providers[0];

function emptyAccount(provider = "gitHub") {
    const kind = providerOf(provider);
    return { id: "", displayName: "", provider, host: kind.host, owners: [], credential: { kind: "cli", variable: null, cliEnvironment: {} }, protocol: "https" };
}

function credentialText(account) {
    const credential = account.credential ?? { kind: "none" };
    if (credential.kind === "environmentVariable") return `token from $${credential.variable}`;
    if (credential.kind === "cli") {
        const profile = Object.entries(credential.cliEnvironment ?? {}).map(([name, value]) => `${name}=${value}`).join(", ");
        return `${providerOf(account.provider).cliName}${profile ? ` with ${profile}` : ", default profile"}`;
    }
    return "no sign-in (public repositories)";
}

export default function RepositorySettings() {
    const alta = useAlta();
    const saved = useRpc("accounts.get", {});
    const [accounts, setAccounts] = useState(null);
    const [editing, setEditing] = useState(null);        // { index, draft } ; index -1 for a new account
    const [checks, setChecks] = useState({});
    const [problem, setProblem] = useState(null);
    const [busy, setBusy] = useState(false);
    const [importResult, setImportResult] = useState(null);

    useEffect(() => { if (saved.data && accounts === null) setAccounts(saved.data); }, [saved.data, accounts]);

    async function save(list) {
        setBusy(true);
        setProblem(null);
        try {
            const result = await alta.rpc.invoke("accounts.save", { accounts: list }, { signal: alta.closed });
            setAccounts(result);
            return true;
        } catch (error) {
            if (!alta.closed.aborted) setProblem(error.message);
            return false;
        } finally {
            setBusy(false);
        }
    }

    async function check(key, account) {
        setChecks(current => ({ ...current, [key]: { pending: true } }));
        try {
            const result = await alta.rpc.invoke("accounts.check", { account }, { signal: alta.closed });
            setChecks(current => ({ ...current, [key]: result }));
        } catch (error) {
            if (!alta.closed.aborted) setChecks(current => ({ ...current, [key]: { ok: false, message: error.message } }));
        }
    }

    async function importFromSpace() {
        setProblem(null);
        try {
            setImportResult(await alta.rpc.invoke("accounts.import", {}, { signal: alta.closed }));
        } catch (error) {
            if (!alta.closed.aborted) setProblem(error.message);
        }
    }

    async function commitDraft() {
        const list = [...accounts];
        if (editing.index < 0) list.push(editing.draft);
        else list[editing.index] = editing.draft;
        if (await save(list)) setEditing(null);
    }

    if (saved.error) return html`<${Callout} intent="danger" title="The accounts could not be read">${saved.error.message}<//>`;
    if (accounts === null) return html`<p className="alta-muted">Loading…</p>`;

    return html`
        <div style=${{ display: "flex", flexDirection: "column", gap: 12, maxWidth: 820 }}>
            <p className="alta-muted" style=${{ margin: 0 }}>
                The accounts whose repositories the Repositories tab lists. An account names where its token comes from: an environment
                variable or the profile of a CLI. No token is ever saved.
            </p>
            ${problem && !editing && html`<${Callout} intent="danger" icon="error">${problem}<//>`}
            ${accounts.length === 0 && !editing && html`<${Callout} icon="info-sign">No account yet. Add one, or import the GitHub and Azure profiles of the space.<//>`}
            ${accounts.map((account, index) => editing?.index === index
                ? html`<${AccountEditor} key=${account.id} draft=${editing.draft} busy=${busy} problem=${problem} isNew=${false}
                        onChange=${draft => setEditing({ index, draft })} onSave=${commitDraft} onCancel=${() => { setEditing(null); setProblem(null); }}
                        onCheck=${() => check("draft", editing.draft)} check=${checks.draft} />`
                : html`<${AccountCard} key=${account.id} account=${account} check=${checks[account.id]} disabled=${busy || editing !== null}
                        onEdit=${() => { setEditing({ index, draft: structuredClone(account) }); setChecks(current => ({ ...current, draft: undefined })); }}
                        onCheck=${() => check(account.id, account)}
                        onRemove=${() => save(accounts.filter((_, other) => other !== index))} />`)}
            ${editing?.index === -1 && html`<${AccountEditor} draft=${editing.draft} busy=${busy} problem=${problem} isNew=${true}
                    onChange=${draft => setEditing({ index: -1, draft })} onSave=${commitDraft} onCancel=${() => { setEditing(null); setProblem(null); }}
                    onCheck=${() => check("draft", editing.draft)} check=${checks.draft} />`}
            ${!editing && html`
                <div style=${{ display: "flex", gap: 8, flexWrap: "wrap" }}>
                    <${Button} icon="add" intent="primary" onClick=${() => { setEditing({ index: -1, draft: emptyAccount() }); setChecks(current => ({ ...current, draft: undefined })); }}>Add account<//>
                    <${Button} icon="import" onClick=${importFromSpace}>Import from the space<//>
                    <${Button} icon="folder-open" variant="minimal" onClick=${() => alta.host.openCanvas("repositories")}>Open the Repositories tab<//>
                </div>`}
            ${importResult && !editing && html`
                <${Card} compact>
                    <strong>Import from space “${importResult.spaceName}”</strong>
                    ${importResult.proposals.length === 0
                        ? html`<p className="alta-muted">This space has no GitHub or Azure CLI profile that is not an account already (Settings › Spaces › identity).</p>`
                        : importResult.proposals.map(proposal => html`
                            <div key=${proposal.id} style=${{ display: "flex", gap: 8, alignItems: "center", marginTop: 8 }}>
                                <span style=${{ flex: 1 }}>${proposal.displayName} <span className="alta-muted">· ${credentialText(proposal)}</span></span>
                                <${Button} size="small" icon="add" onClick=${() => { setEditing({ index: -1, draft: structuredClone(proposal) }); setImportResult(null); }}>Review and add<//>
                            </div>`)}
                    <p className="alta-muted" style=${{ marginBottom: 0 }}>Another space is imported by switching to it first.</p>
                <//>`}
        </div>`;
}

function AccountCard({ account, check, disabled, onEdit, onCheck, onRemove }) {
    return html`
        <${Card} compact>
            <div style=${{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
                <strong>${account.displayName}</strong>
                <${Tag} minimal>${providerOf(account.provider).label}<//>
                <span className="alta-muted">${account.host}</span>
                <span style=${{ flex: 1 }} />
                <${Button} size="small" icon="endorsed" disabled=${disabled} loading=${check?.pending} onClick=${onCheck}>Check<//>
                <${Button} size="small" icon="edit" disabled=${disabled} onClick=${onEdit}>Edit<//>
                <${Button} size="small" icon="trash" intent="danger" variant="minimal" disabled=${disabled} onClick=${onRemove} title="Remove the account" />
            </div>
            <div className="alta-muted" style=${{ marginTop: 4 }}>
                ${account.owners.length > 0 ? `Owners: ${account.owners.join(", ")}` : "Every repository the token sees"} · ${credentialText(account)} · ${account.protocol === "ssh" ? "SSH" : "HTTPS"} clones
            </div>
            ${check && !check.pending && html`<${CheckLine} check=${check} />`}
        <//>`;
}

function CheckLine({ check }) {
    return html`<div style=${{ marginTop: 6 }}><${Tag} minimal intent=${check.ok ? "success" : "danger"} icon=${check.ok ? "tick" : "cross"}>${check.message}<//></div>`;
}

function AccountEditor({ draft, busy, problem, isNew, onChange, onSave, onCancel, onCheck, check }) {
    const ids = { id: useId(), name: useId(), provider: useId(), host: useId(), owners: useId(), kind: useId(), variable: useId(), cli: useId(), protocol: useId() };
    const kind = providerOf(draft.provider);
    const credential = draft.credential ?? { kind: "none" };
    const [ownersText, setOwnersText] = useState((draft.owners ?? []).join(", "));
    const [cliText, setCliText] = useState(Object.entries(credential.cliEnvironment ?? {}).map(([name, value]) => `${name}=${value}`).join("\n"));
    const set = changes => onChange({ ...draft, ...changes });
    const setCredential = changes => set({ credential: { ...credential, ...changes } });

    function setProvider(value) {
        // One change: two in a row would each start from the same draft, and the second would undo the first.
        const kind = value === "azureDevOps" && credential.kind === "none" ? "cli" : credential.kind;
        set({ provider: value, host: providerOf(value).host, credential: { ...credential, kind } });
    }

    function setCli(text) {
        setCliText(text);
        const environment = {};
        for (const line of text.split("\n")) {
            const at = line.indexOf("=");
            if (at > 0) environment[line.slice(0, at).trim()] = line.slice(at + 1).trim();
        }
        setCredential({ cliEnvironment: environment });
    }

    function setOwners(text) {
        setOwnersText(text);
        set({ owners: text.split(/[,\s]+/).map(owner => owner.trim()).filter(Boolean) });
    }

    const field = { style: { flex: "1 1 240px", margin: 0 } };
    return html`
        <${Card} compact style=${{ display: "flex", flexDirection: "column", gap: 10 }}>
            <strong>${isNew ? "New account" : `Edit ${draft.displayName || draft.id}`}</strong>
            <div style=${{ display: "flex", gap: 12, flexWrap: "wrap" }}>
                <${FormGroup} ...${field} label="Service" labelFor=${ids.provider}>
                    <${HTMLSelect} id=${ids.provider} fill value=${draft.provider} onChange=${event => setProvider(event.target.value)}
                        options=${providers.map(item => ({ value: item.value, label: item.label }))} />
                <//>
                <${FormGroup} ...${field} label="Host" labelFor=${ids.host} helperText=${draft.provider === "gitHub" ? "github.com, or the host of GitHub Enterprise" : "dev.azure.com"}>
                    <${InputGroup} id=${ids.host} value=${draft.host} disabled=${draft.provider === "azureDevOps"} onChange=${event => set({ host: event.target.value })} />
                <//>
            </div>
            <div style=${{ display: "flex", gap: 12, flexWrap: "wrap" }}>
                <${FormGroup} ...${field} label="Id" labelFor=${ids.id} helperText="Lower-case letters, digits and -, such as github-work">
                    <${InputGroup} id=${ids.id} value=${draft.id} onChange=${event => set({ id: event.target.value.toLowerCase() })} />
                <//>
                <${FormGroup} ...${field} label="Name" labelFor=${ids.name}>
                    <${InputGroup} id=${ids.name} value=${draft.displayName} placeholder=${kind.label} onChange=${event => set({ displayName: event.target.value })} />
                <//>
            </div>
            <${FormGroup} style=${{ margin: 0 }} label=${draft.provider === "azureDevOps" ? "Organizations" : "Owners"} labelFor=${ids.owners}
                helperText=${draft.provider === "azureDevOps" ? "Required: the organizations of dev.azure.com, separated by commas" : "Users or organizations to list, separated by commas; empty: every repository the token sees"}>
                <${InputGroup} id=${ids.owners} value=${ownersText} onChange=${event => setOwners(event.target.value)} />
            <//>
            <div style=${{ display: "flex", gap: 12, flexWrap: "wrap" }}>
                <${FormGroup} ...${field} label="Token from" labelFor=${ids.kind}>
                    <${HTMLSelect} id=${ids.kind} fill value=${credential.kind} onChange=${event => setCredential({ kind: event.target.value })}
                        options=${[
                            { value: "cli", label: kind.cliName },
                            { value: "environmentVariable", label: "An environment variable" },
                            ...(draft.provider === "gitHub" ? [{ value: "none", label: "No sign-in (public repositories only)" }] : []),
                        ]} />
                <//>
                <${FormGroup} ...${field} label="Clone with" labelFor=${ids.protocol}>
                    <${HTMLSelect} id=${ids.protocol} fill value=${draft.protocol} onChange=${event => set({ protocol: event.target.value })}
                        options=${[{ value: "https", label: "HTTPS (the token of the account)" }, { value: "ssh", label: "SSH (your SSH keys)" }]} />
                <//>
            </div>
            ${credential.kind === "environmentVariable" && html`
                <${FormGroup} style=${{ margin: 0 }} label="Variable name" labelFor=${ids.variable}
                    helperText="The name only, never the token. It is read from the environment CodeAlta was started with.">
                    <${InputGroup} id=${ids.variable} value=${credential.variable ?? ""} placeholder=${kind.variable}
                        onChange=${event => setCredential({ variable: event.target.value })} />
                <//>`}
            ${credential.kind === "cli" && html`
                <${FormGroup} style=${{ margin: 0 }} label="CLI profile" labelFor=${ids.cli}
                    helperText=${`One NAME=value per line, such as ${kind.cli}=~/.config/${draft.provider === "gitHub" ? "gh-work" : "azure-work"}; empty for the default profile.`}>
                    <${TextArea} id=${ids.cli} fill rows=${2} value=${cliText} placeholder=${`${kind.cli}=`} onChange=${event => setCli(event.target.value)} />
                <//>`}
            ${check && !check.pending && html`<${CheckLine} check=${check} />`}
            ${problem && html`<${Callout} intent="danger" icon="error">${problem}<//>`}
            <div style=${{ display: "flex", gap: 8 }}>
                <${Button} intent="primary" icon="floppy-disk" loading=${busy} onClick=${onSave}>Save<//>
                <${Button} icon="endorsed" loading=${check?.pending} onClick=${onCheck}>Check<//>
                <${Button} variant="minimal" onClick=${onCancel}>Cancel<//>
            </div>
        <//>`;
}
