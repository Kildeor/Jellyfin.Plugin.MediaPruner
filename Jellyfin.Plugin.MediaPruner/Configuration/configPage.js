const pluginId = '6f0b1f3e-5a4c-4c1e-9d8b-2b7f2e6a9c41';

const textFields = ['RadarrUrl', 'RadarrApiKey', 'SonarrUrl', 'SonarrApiKey', 'ProtectedTags', 'DateSource', 'PendingTagName',
    'PendingCollectionName'];
const numberFields = ['DaysBeforeDeletion', 'KeepIfPlayedWithinDays', 'MaxDeletionsPerRun', 'WarningDays'];
const boolFields = ['RadarrEnabled', 'SonarrEnabled', 'KeepFavorites', 'KeepFavoriteEpisodes', 'OnlyWatched',
    'KeepContinuingSeries', 'AutoDelete', 'DryRun', 'DeleteFiles', 'AddImportExclusion', 'RefreshLibraryAfterDelete',
    'TagPendingItems', 'CollectionPendingItems'];

// API responses may be PascalCase or camelCase: normalize to camelCase.
function camel(obj) {
    if (Array.isArray(obj)) return obj.map(camel);
    if (obj && typeof obj === 'object') {
        const out = {};
        Object.keys(obj).forEach(k => { out[k.charAt(0).toLowerCase() + k.slice(1)] = camel(obj[k]); });
        return out;
    }
    return obj;
}

function esc(value) {
    return String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

function formatSize(bytes) {
    if (!bytes) return '—';
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    let i = 0;
    let n = bytes;
    while (n >= 1024 && i < units.length - 1) { n /= 1024; i++; }
    return n.toFixed(i >= 3 ? 2 : 0) + ' ' + units[i];
}

function formatDate(value) {
    if (!value) return '—';
    const d = new Date(value);
    return isNaN(d) ? '—' : d.toLocaleDateString();
}

function formatDateTime(value) {
    if (!value) return '—';
    const d = new Date(value);
    return isNaN(d) ? '—' : d.toLocaleString();
}

function sourceName(source) {
    return (source === 1 || String(source).toLowerCase() === 'sonarr') ? 'Sonarr' : 'Radarr';
}

// Older history entries were stored with French mode labels.
function modeName(mode) {
    return ({ Manuel: 'Manual', Simulation: 'Dry run' })[mode] || mode;
}

function api(method, path, body) {
    const opts = { type: method, url: ApiClient.getUrl(path) };
    if (body !== undefined) {
        opts.data = JSON.stringify(body);
        opts.contentType = 'application/json';
    }
    if (method === 'GET' || path === 'MediaPruner/Delete' || path === 'MediaPruner/DeleteEligible' || path === 'MediaPruner/TestConnection') {
        opts.dataType = 'json';
    }
    return ApiClient.ajax(opts).then(r => camel(r));
}

function confirmAction(text) {
    if (window.Dashboard && typeof Dashboard.confirm === 'function') {
        return new Promise(resolve => {
            try {
                const p = Dashboard.confirm(text, 'Media Pruner', ok => resolve(ok));
                if (p && typeof p.then === 'function') p.then(() => resolve(true), () => resolve(false));
            } catch (e) {
                resolve(window.confirm(text));
            }
        });
    }
    return Promise.resolve(window.confirm(text));
}

function notify(text) {
    if (window.Dashboard && typeof Dashboard.alert === 'function') {
        Dashboard.alert({ title: 'Media Pruner', message: text });
    } else {
        window.alert(text);
    }
}

function describeResult(r) {
    const prefix = r.dryRun ? '[Dry run] ' : '';
    let msg = `${prefix}${r.deleted} item(s) deleted, ${formatSize(r.freedBytes)} freed.`;
    if (r.failed) msg += ` ${r.failed} failed, see the history.`;
    return msg;
}

export default function (view) {
    let candidates = [];
    const selected = new Set();

    const $ = sel => view.querySelector(sel);

    // ---------- Tabs ----------
    view.querySelectorAll('.mc-tab').forEach(tab => {
        tab.addEventListener('click', () => showTab(tab.getAttribute('data-tab')));
    });

    function showTab(name) {
        view.querySelectorAll('.mc-tab').forEach(t => t.classList.toggle('active', t.getAttribute('data-tab') === name));
        view.querySelectorAll('.mc-panel').forEach(p => p.classList.toggle('active', p.getAttribute('data-panel') === name));
        if (name === 'candidates') loadCandidates();
        if (name === 'history') loadHistory();
    }

    // ---------- Options ----------
    function loadConfig() {
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(pluginId).then(config => {
            textFields.forEach(f => { $('#' + f).value = config[f] ?? ''; });
            numberFields.forEach(f => { $('#' + f).value = config[f] ?? 0; });
            boolFields.forEach(f => { $('#' + f).checked = !!config[f]; });
            const ds = String(config.DateSource);
            $('#DateSource').value = (ds === '1' || ds === 'Jellyfin') ? 'Jellyfin' : 'Arr';
            Dashboard.hideLoadingMsg();
        });
    }

    $('#MediaPrunerConfigForm').addEventListener('submit', e => {
        e.preventDefault();
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(pluginId).then(config => {
            textFields.forEach(f => { config[f] = $('#' + f).value.trim(); });
            numberFields.forEach(f => { config[f] = Math.max(0, parseInt($('#' + f).value, 10) || 0); });
            boolFields.forEach(f => { config[f] = $('#' + f).checked; });
            return ApiClient.updatePluginConfiguration(pluginId, config);
        }).then(result => {
            Dashboard.processPluginConfigurationUpdateResult(result);
        });
        return false;
    });

    view.querySelectorAll('.btnTest').forEach(btn => {
        btn.addEventListener('click', () => {
            const kind = btn.getAttribute('data-kind');
            const out = $('#' + kind + 'TestResult');
            out.className = 'mc-test';
            out.textContent = 'Testing…';
            api('POST', 'MediaPruner/TestConnection', {
                Kind: kind,
                Url: $('#' + kind + 'Url').value.trim(),
                ApiKey: $('#' + kind + 'ApiKey').value.trim()
            }).then(r => {
                out.className = 'mc-test ' + (r.success ? 'mc-ok' : 'mc-err');
                out.textContent = (r.success ? '✔ ' : '✖ ') + r.message;
            }).catch(() => {
                out.className = 'mc-test mc-err';
                out.textContent = '✖ Error during test';
            });
        });
    });

    // ---------- Pending deletion ----------
    function loadCandidates() {
        const target = $('#mcCandidates');
        target.innerHTML = '<div class="mc-empty">Loading…</div>';
        $('#mcErrors').textContent = '';
        const all = $('#chkShowAll').checked;
        api('GET', 'MediaPruner/Candidates?all=' + all).then(r => {
            candidates = r.items || [];
            selected.clear();
            $('#mcErrors').innerHTML = (r.errors || []).map(esc).join('<br>');
            renderCandidates();
        }).catch(() => {
            target.innerHTML = '<div class="mc-empty mc-err">Unable to load the list. Check the configuration.</div>';
        });
    }

    function renderCandidates() {
        const showExcluded = $('#chkShowExcluded').checked;
        const list = candidates.filter(c => showExcluded || (c.exclusionReasons || []).length === 0);
        const eligible = candidates.filter(c => c.isEligible);
        const upcoming = candidates.filter(c => !c.isEligible && (c.exclusionReasons || []).length === 0);
        const excluded = candidates.filter(c => (c.exclusionReasons || []).length > 0);

        $('#mcSummary').innerHTML =
            `<div class="mc-stat"><b>${eligible.length}</b>eligible now</div>` +
            `<div class="mc-stat"><b>${formatSize(eligible.reduce((s, c) => s + (c.sizeOnDisk || 0), 0))}</b>to free</div>` +
            `<div class="mc-stat"><b>${upcoming.length}</b>upcoming</div>` +
            `<div class="mc-stat"><b>${excluded.length}</b>excluded</div>`;

        $('#btnDeleteEligible').disabled = eligible.length === 0;
        updateSelectionButton();

        if (list.length === 0) {
            $('#mcCandidates').innerHTML = '<div class="mc-empty">Nothing due for deletion soon 🎉</div>';
            return;
        }

        let html = '<table class="mc-table"><thead><tr>' +
            '<th><input type="checkbox" class="mc-selall" title="Select all"></th><th></th><th>Title</th><th>Source</th>' +
            '<th>Added</th><th>Deletion</th><th>Size</th><th>Status</th><th>Actions</th>' +
            '</tr></thead><tbody>';

        list.forEach(c => {
            const reasons = c.exclusionReasons || [];
            const src = sourceName(c.source);
            let due;
            if (c.daysRemaining <= 0) {
                due = '<span class="mc-badge mc-due">Now</span>';
            } else {
                due = `<span class="mc-badge mc-soon">In ${c.daysRemaining} d</span><div class="mc-small">${formatDate(c.deletionDate)}</div>`;
            }
            const status = [];
            if (c.isWatched) status.push('<span class="mc-badge mc-reason">Watched</span>');
            if (c.isFavorite) status.push('<span class="mc-badge mc-reason">★ Favorite</span>');
            if (!c.jellyfinId) status.push('<span class="mc-badge mc-reason">Not in Jellyfin</span>');
            reasons.forEach(r => status.push(`<span class="mc-badge mc-reason" title="Excluded from automatic deletion">⛔ ${esc(r)}</span>`));
            const poster = c.posterUrl ? `<img class="mc-poster" loading="lazy" src="${esc(c.posterUrl)}" alt="">` : '<span class="mc-poster"></span>';
            const title = c.jellyfinId
                ? `<a is="emby-linkbutton" class="button-link" href="#/details?id=${esc(c.jellyfinId)}">${esc(c.title)}</a>`
                : esc(c.title);

            html += `<tr class="${reasons.length ? 'mc-excluded' : ''}">` +
                `<td><input type="checkbox" class="mc-sel" data-key="${esc(c.key)}" ${selected.has(c.key) ? 'checked' : ''}></td>` +
                `<td>${poster}</td>` +
                `<td>${title}${c.year ? ` <span class="mc-small">(${c.year})</span>` : ''}` +
                (c.lastPlayed ? `<div class="mc-small">Last played: ${formatDate(c.lastPlayed)}</div>` : '') + '</td>' +
                `<td><span class="mc-badge mc-${src.toLowerCase()}">${src}</span></td>` +
                `<td>${formatDate(c.addedDate)}</td>` +
                `<td>${due}</td>` +
                `<td>${formatSize(c.sizeOnDisk)}</td>` +
                `<td>${status.join('') || '—'}</td>` +
                '<td class="mc-actions" style="white-space:nowrap">' +
                `<button is="emby-button" type="button" class="raised mc-protect" data-key="${esc(c.key)}" data-protected="${c.isProtected}">` +
                `<span>${c.isProtected ? 'Unprotect' : 'Protect'}</span></button>` +
                `<button is="emby-button" type="button" class="raised button-delete mc-delete" data-key="${esc(c.key)}"><span>Delete</span></button>` +
                '</td></tr>';
        });

        html += '</tbody></table>';
        $('#mcCandidates').innerHTML = html;
    }

    function updateSelectionButton() {
        const btn = $('#btnDeleteSelected');
        btn.disabled = selected.size === 0;
        btn.querySelector('span').textContent = selected.size ? `Delete selected (${selected.size})` : 'Delete selected';
    }

    function deleteKeys(keys) {
        const items = candidates.filter(c => keys.includes(c.key));
        const names = items.slice(0, 10).map(c => '• ' + c.title).join('\n') + (items.length > 10 ? `\n… and ${items.length - 10} more` : '');
        const filesWarn = $('#DeleteFiles').checked ? '\n\nFiles will be deleted from disk.' : '';
        confirmAction(`Permanently delete ${keys.length} item(s) from Radarr/Sonarr?\n\n${names}${filesWarn}`).then(ok => {
            if (!ok) return;
            Dashboard.showLoadingMsg();
            api('POST', 'MediaPruner/Delete', { Keys: keys }).then(r => {
                Dashboard.hideLoadingMsg();
                notify(describeResult(r));
                loadCandidates();
            }).catch(() => {
                Dashboard.hideLoadingMsg();
                notify('Error during deletion.');
            });
        });
    }

    $('#mcCandidates').addEventListener('change', e => {
        const t = e.target;
        if (t.classList.contains('mc-sel')) {
            if (t.checked) selected.add(t.getAttribute('data-key')); else selected.delete(t.getAttribute('data-key'));
            updateSelectionButton();
        } else if (t.classList.contains('mc-selall')) {
            view.querySelectorAll('.mc-sel').forEach(cb => {
                cb.checked = t.checked;
                if (t.checked) selected.add(cb.getAttribute('data-key')); else selected.delete(cb.getAttribute('data-key'));
            });
            updateSelectionButton();
        }
    });

    $('#mcCandidates').addEventListener('click', e => {
        const del = e.target.closest('.mc-delete');
        if (del) {
            deleteKeys([del.getAttribute('data-key')]);
            return;
        }
        const prot = e.target.closest('.mc-protect');
        if (prot) {
            const isProtected = prot.getAttribute('data-protected') === 'true';
            api('POST', 'MediaPruner/Protect', { Key: prot.getAttribute('data-key'), Protected: !isProtected })
                .then(loadCandidates)
                .catch(() => notify('Unable to change protection.'));
        }
    });

    $('#btnRefresh').addEventListener('click', loadCandidates);
    $('#chkShowExcluded').addEventListener('change', renderCandidates);
    $('#chkShowAll').addEventListener('change', loadCandidates);
    $('#btnDeleteSelected').addEventListener('click', () => deleteKeys(Array.from(selected)));
    $('#btnDeleteEligible').addEventListener('click', () => {
        const eligible = candidates.filter(c => c.isEligible);
        confirmAction(`Delete the ${eligible.length} eligible item(s) now?`).then(ok => {
            if (!ok) return;
            Dashboard.showLoadingMsg();
            api('POST', 'MediaPruner/DeleteEligible').then(r => {
                Dashboard.hideLoadingMsg();
                notify(describeResult(r));
                loadCandidates();
            }).catch(() => {
                Dashboard.hideLoadingMsg();
                notify('Error during deletion.');
            });
        });
    });

    // ---------- History ----------
    function loadHistory() {
        const target = $('#mcHistory');
        target.innerHTML = '<div class="mc-empty">Loading…</div>';
        api('GET', 'MediaPruner/History').then(entries => {
            if (!entries || entries.length === 0) {
                target.innerHTML = '<div class="mc-empty">No deletions yet.</div>';
                return;
            }
            let html = '<table class="mc-table"><thead><tr><th>Date</th><th>Title</th><th>Source</th><th>Size</th><th>Mode</th><th>Result</th></tr></thead><tbody>';
            entries.forEach(h => {
                const src = sourceName(h.source);
                html += `<tr><td>${formatDateTime(h.date)}</td><td>${esc(h.title)}</td>` +
                    `<td><span class="mc-badge mc-${src.toLowerCase()}">${src}</span></td>` +
                    `<td>${formatSize(h.sizeOnDisk)}</td><td>${esc(modeName(h.mode))}</td>` +
                    `<td>${h.success ? '<span class="mc-ok">✔ OK</span>' : `<span class="mc-err">✖ ${esc(h.error)}</span>`}</td></tr>`;
            });
            target.innerHTML = html + '</tbody></table>';
        }).catch(() => {
            target.innerHTML = '<div class="mc-empty mc-err">Unable to load the history.</div>';
        });
    }

    $('#btnRefreshHistory').addEventListener('click', loadHistory);
    $('#btnClearHistory').addEventListener('click', () => {
        confirmAction('Clear the entire history?').then(ok => {
            if (ok) api('DELETE', 'MediaPruner/History').then(loadHistory);
        });
    });

    view.addEventListener('viewshow', loadConfig);
}
