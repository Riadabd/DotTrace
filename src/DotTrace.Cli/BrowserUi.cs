internal static class BrowserUi
{
    public const string Html =
        """
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>DotTrace Explorer</title>
          <style>
            :root {
              --bg: #f4f6f8;
              --panel: #ffffff;
              --ink: #17202a;
              --muted: #667085;
              --border: #d4dbe6;
              --accent: #0b5cad;
              --accent-ink: #ffffff;
              --source: #0b5cad;
              --external: #946200;
              --cycle: #b4235a;
              --repeated: #6d4bc2;
              --truncated: #8a6400;
              --unresolved: #c22f24;
              --shadow: 0 1px 2px rgba(16, 24, 40, 0.06);
            }

            * { box-sizing: border-box; }

            body {
              margin: 0;
              min-height: 100vh;
              color: var(--ink);
              background: var(--bg);
              font-family: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
            }

            button, input, select {
              font: inherit;
            }

            button {
              border: 1px solid var(--border);
              border-radius: 6px;
              background: var(--panel);
              color: var(--ink);
              cursor: pointer;
              min-height: 34px;
              padding: 6px 10px;
            }

            button:hover { border-color: #98a2b3; }
            button.active { background: #253244; color: #ffffff; border-color: #253244; }
            button:disabled { cursor: not-allowed; color: var(--muted); background: #eef1f5; }

            .app {
              display: grid;
              grid-template-columns: minmax(320px, 390px) minmax(0, 1fr);
              min-height: 100vh;
            }

            .sidebar {
              border-right: 1px solid var(--border);
              background: #fbfcfe;
              padding: 16px;
              overflow: auto;
            }

            .main {
              padding: 16px;
              min-width: 0;
              display: grid;
              grid-template-rows: auto minmax(0, 1fr);
              gap: 12px;
            }

            h1 {
              margin: 0 0 12px;
              font-size: 1.25rem;
              letter-spacing: 0;
            }

            h2 {
              margin: 0;
              font-size: 1rem;
              letter-spacing: 0;
            }

            label {
              display: block;
              margin: 12px 0 6px;
              color: var(--muted);
              font-size: 0.82rem;
              font-weight: 650;
            }

            input, select {
              width: 100%;
              min-height: 36px;
              border: 1px solid var(--border);
              border-radius: 6px;
              background: var(--panel);
              color: var(--ink);
              padding: 7px 9px;
            }

            .db-path {
              color: var(--muted);
              font: 0.78rem ui-monospace, SFMono-Regular, Consolas, monospace;
              overflow-wrap: anywhere;
              margin-bottom: 12px;
            }

            .results {
              display: grid;
              gap: 6px;
              margin-top: 12px;
            }

            .result {
              width: 100%;
              text-align: left;
              min-height: 0;
              padding: 8px;
              border-radius: 6px;
              background: var(--panel);
              box-shadow: var(--shadow);
            }

            .result.active {
              border-color: var(--accent);
              box-shadow: 0 0 0 2px rgba(11, 92, 173, 0.12);
            }

            .signature {
              display: block;
              font: 0.84rem ui-monospace, SFMono-Regular, Consolas, monospace;
              overflow-wrap: anywhere;
            }

            .meta {
              display: flex;
              flex-wrap: wrap;
              gap: 6px 10px;
              margin-top: 5px;
              color: var(--muted);
              font-size: 0.76rem;
            }

            .toolbar {
              display: flex;
              align-items: end;
              justify-content: space-between;
              gap: 12px;
              background: var(--panel);
              border: 1px solid var(--border);
              border-radius: 8px;
              box-shadow: var(--shadow);
              padding: 12px;
            }

            .selected {
              min-width: 0;
            }

            .selected .signature {
              font-size: 0.9rem;
            }

            .controls {
              display: flex;
              align-items: end;
              gap: 8px;
              flex-wrap: wrap;
              justify-content: flex-end;
            }

            .controls label {
              margin-top: 0;
            }

            .depth {
              width: 94px;
            }

            .tabs {
              display: flex;
              gap: 4px;
            }

            .tree-shell {
              min-height: 0;
              background: var(--panel);
              border: 1px solid var(--border);
              border-radius: 8px;
              box-shadow: var(--shadow);
              overflow: auto;
            }

            .placeholder, .error {
              padding: 18px;
              color: var(--muted);
            }

            .error {
              color: var(--unresolved);
            }

            .tree-section {
              padding: 14px 16px 4px;
              border-bottom: 1px solid var(--border);
              color: var(--muted);
              font-size: 0.8rem;
              font-weight: 700;
              text-transform: uppercase;
            }

            .tree {
              min-width: max-content;
              padding: 10px 12px 18px;
              font: 0.84rem ui-monospace, SFMono-Regular, Consolas, monospace;
            }

            .tree-row {
              display: grid;
              grid-template-columns: 24px minmax(360px, 1fr) auto;
              align-items: start;
              gap: 8px;
              min-height: 28px;
              padding: 3px 8px 3px calc(8px + var(--depth, 0) * 22px);
              border-radius: 6px;
              white-space: nowrap;
            }

            .tree-row:hover {
              background: #f1f5f9;
            }

            .twisty {
              width: 22px;
              min-height: 22px;
              padding: 0;
              line-height: 1;
              border-color: transparent;
              background: transparent;
            }

            .node-text {
              overflow-wrap: anywhere;
              white-space: normal;
            }

            .kind {
              border-radius: 999px;
              border: 1px solid currentColor;
              padding: 1px 6px;
              font-size: 0.72rem;
              line-height: 1.6;
            }

            .kind-source { color: var(--source); }
            .kind-external { color: var(--external); }
            .kind-cycle { color: var(--cycle); }
            .kind-repeated { color: var(--repeated); }
            .kind-truncated { color: var(--truncated); }
            .kind-unresolved { color: var(--unresolved); }

            .hidden {
              display: none;
            }

            @media (max-width: 860px) {
              .app {
                grid-template-columns: 1fr;
              }

              .sidebar {
                border-right: 0;
                border-bottom: 1px solid var(--border);
                max-height: 46vh;
              }

              .toolbar {
                align-items: stretch;
                flex-direction: column;
              }

              .controls {
                justify-content: flex-start;
              }
            }
          </style>
        </head>
        <body>
          <div class="app">
            <aside class="sidebar">
              <h1>DotTrace Explorer</h1>
              <div id="dbPath" class="db-path"></div>

              <label for="snapshotSelect">Snapshot</label>
              <select id="snapshotSelect"></select>

              <label for="symbolSearch">Method search</label>
              <input id="symbolSearch" type="search" autocomplete="off" placeholder="Type namespace, class, or method" />

              <div id="symbolResults" class="results"></div>
            </aside>

            <main class="main">
              <section class="toolbar">
                <div class="selected">
                  <h2>Selected method</h2>
                  <span id="selectedSignature" class="signature">No method selected</span>
                  <div id="selectedMeta" class="meta"></div>
                </div>
                <div class="controls">
                  <div>
                    <label for="maxDepth">Max depth</label>
                    <input id="maxDepth" class="depth" type="number" min="1" placeholder="All" />
                  </div>
                  <div>
                    <label>View</label>
                    <div class="tabs" role="tablist" aria-label="Call tree view">
                      <button type="button" class="active" data-view="callees">Callees</button>
                      <button type="button" data-view="callers">Callers</button>
                      <button type="button" data-view="both">Both</button>
                    </div>
                  </div>
                  <button id="refreshTree" type="button" disabled>Refresh</button>
                </div>
              </section>

              <section id="treeShell" class="tree-shell">
                <div class="placeholder">Select a source method to render its call tree.</div>
              </section>
            </main>
          </div>

          <script>
            const state = {
              config: null,
              snapshots: [],
              selectedSymbol: null,
              selectedView: 'callees',
              searchTimer: 0
            };

            const elements = {
              dbPath: document.getElementById('dbPath'),
              snapshotSelect: document.getElementById('snapshotSelect'),
              symbolSearch: document.getElementById('symbolSearch'),
              symbolResults: document.getElementById('symbolResults'),
              selectedSignature: document.getElementById('selectedSignature'),
              selectedMeta: document.getElementById('selectedMeta'),
              maxDepth: document.getElementById('maxDepth'),
              refreshTree: document.getElementById('refreshTree'),
              treeShell: document.getElementById('treeShell')
            };

            async function api(path) {
              const response = await fetch(path, { headers: { accept: 'application/json' } });
              if (response.ok) {
                return response.json();
              }

              let message = `${response.status} ${response.statusText}`;
              try {
                const problem = await response.json();
                message = problem.detail || problem.title || message;
              } catch {
              }

              throw new Error(message);
            }

            function selectedSnapshot() {
              const value = elements.snapshotSelect.value;
              return value ? Number(value) : null;
            }

            function setBusy(container, text) {
              container.replaceChildren();
              const node = document.createElement('div');
              node.className = 'placeholder';
              node.textContent = text;
              container.appendChild(node);
            }

            function setError(container, error) {
              container.replaceChildren();
              const node = document.createElement('div');
              node.className = 'error';
              node.textContent = error.message || String(error);
              container.appendChild(node);
            }

            function formatLocation(location) {
              if (!location || !location.filePath) return '';
              return `${location.filePath}:${location.line}:${location.column}`;
            }

            function renderMeta(symbol) {
              const values = [];
              if (symbol.projectName) values.push(symbol.projectName);
              values.push(`${symbol.directCallerCount} callers`);
              values.push(`${symbol.directCalleeCount} callees`);
              const location = formatLocation(symbol.location);
              if (location) values.push(location);
              return values;
            }

            async function loadConfig() {
              state.config = await api('/api/config');
              elements.dbPath.textContent = state.config.dbPath;
              if (state.config.initialMaxDepth) {
                elements.maxDepth.value = state.config.initialMaxDepth;
              }
            }

            async function loadSnapshots() {
              state.snapshots = await api('/api/snapshots');
              elements.snapshotSelect.replaceChildren();

              for (const snapshot of state.snapshots) {
                const option = document.createElement('option');
                option.value = snapshot.id;
                option.textContent = `${snapshot.isActive ? '* ' : ''}${snapshot.id} - ${snapshot.createdUtc}`;
                elements.snapshotSelect.appendChild(option);
              }

              const initial = state.config.initialSnapshotId;
              const active = state.snapshots.find(snapshot => snapshot.isActive);
              if (initial) {
                elements.snapshotSelect.value = String(initial);
              } else if (active) {
                elements.snapshotSelect.value = String(active.id);
              }
            }

            async function searchSymbols() {
              const params = new URLSearchParams();
              const snapshot = selectedSnapshot();
              if (snapshot) params.set('snapshot', snapshot);
              params.set('query', elements.symbolSearch.value);
              params.set('pageSize', '60');

              setBusy(elements.symbolResults, 'Searching...');
              try {
                const results = await api(`/api/symbols?${params}`);
                renderResults(results);
              } catch (error) {
                setError(elements.symbolResults, error);
              }
            }

            function renderResults(results) {
              elements.symbolResults.replaceChildren();
              if (results.length === 0) {
                setBusy(elements.symbolResults, 'No source methods matched.');
                return;
              }

              for (const symbol of results) {
                const button = document.createElement('button');
                button.type = 'button';
                button.className = 'result';
                if (state.selectedSymbol && state.selectedSymbol.id === symbol.id) {
                  button.classList.add('active');
                }

                const signature = document.createElement('span');
                signature.className = 'signature';
                signature.textContent = symbol.signatureText;
                button.appendChild(signature);

                const meta = document.createElement('span');
                meta.className = 'meta';
                for (const value of renderMeta(symbol)) {
                  const item = document.createElement('span');
                  item.textContent = value;
                  meta.appendChild(item);
                }
                button.appendChild(meta);

                button.addEventListener('click', () => selectSymbol(symbol));
                elements.symbolResults.appendChild(button);
              }
            }

            async function selectSymbol(symbol) {
              state.selectedSymbol = symbol;
              renderSelected(symbol);
              elements.refreshTree.disabled = false;
              await loadTree();
              await searchSymbols();
            }

            function renderSelected(symbol) {
              elements.selectedSignature.textContent = symbol.signatureText;
              elements.selectedMeta.replaceChildren();
              for (const value of renderMeta(symbol)) {
                const item = document.createElement('span');
                item.textContent = value;
                elements.selectedMeta.appendChild(item);
              }
            }

            async function loadTree() {
              if (!state.selectedSymbol) return;

              const params = new URLSearchParams();
              params.set('symbolId', state.selectedSymbol.id);
              const snapshot = selectedSnapshot();
              if (snapshot) params.set('snapshot', snapshot);
              params.set('view', state.selectedView);
              if (elements.maxDepth.value) params.set('maxDepth', elements.maxDepth.value);

              setBusy(elements.treeShell, 'Rendering tree...');
              try {
                const response = await api(`/api/tree?${params}`);
                renderTree(response.document, response.view);
              } catch (error) {
                setError(elements.treeShell, error);
              }
            }

            function renderTree(document, view) {
              elements.treeShell.replaceChildren();
              if (view === 'both') {
                appendTreeSection('Callers');
                appendTree(document.callersTree.children);
                appendTreeSection('Selected');
                appendTree([document.selectedRoot]);
                appendTreeSection('Callees');
                appendTree(document.calleesTree.children);
                return;
              }

              const root = view === 'callers' ? document.callersTree : document.calleesTree;
              appendTree(root.children);
            }

            function appendTreeSection(label) {
              const section = document.createElement('div');
              section.className = 'tree-section';
              section.textContent = label;
              elements.treeShell.appendChild(section);
            }

            function appendTree(nodes) {
              const tree = document.createElement('div');
              tree.className = 'tree';
              if (!nodes || nodes.length === 0) {
                const empty = document.createElement('div');
                empty.className = 'placeholder';
                empty.textContent = 'No calls found.';
                tree.appendChild(empty);
              } else {
                for (const node of nodes) {
                  appendNode(tree, node, 0);
                }
              }
              elements.treeShell.appendChild(tree);
            }

            function appendNode(parent, node, depth) {
              const row = document.createElement('div');
              row.className = `tree-row kind-${node.kind}`;
              row.style.setProperty('--depth', String(depth));

              const twisty = document.createElement('button');
              twisty.type = 'button';
              twisty.className = 'twisty';
              twisty.textContent = node.children && node.children.length > 0 ? '-' : '';
              twisty.disabled = !node.children || node.children.length === 0;
              row.appendChild(twisty);

              const text = document.createElement('span');
              text.className = 'node-text';
              const location = formatLocation(node.location);
              text.textContent = location ? `${node.displayText}  ${location}` : node.displayText;
              row.appendChild(text);

              const kind = document.createElement('span');
              kind.className = `kind kind-${node.kind}`;
              kind.textContent = node.kind;
              row.appendChild(kind);

              parent.appendChild(row);

              const children = document.createElement('div');
              if (node.children) {
                for (const child of node.children) {
                  appendNode(children, child, depth + 1);
                }
              }
              parent.appendChild(children);

              twisty.addEventListener('click', () => {
                children.classList.toggle('hidden');
                twisty.textContent = children.classList.contains('hidden') ? '+' : '-';
              });
            }

            function bindEvents() {
              elements.symbolSearch.addEventListener('input', () => {
                clearTimeout(state.searchTimer);
                state.searchTimer = setTimeout(searchSymbols, 180);
              });

              elements.snapshotSelect.addEventListener('change', async () => {
                state.selectedSymbol = null;
                elements.refreshTree.disabled = true;
                elements.selectedSignature.textContent = 'No method selected';
                elements.selectedMeta.replaceChildren();
                setBusy(elements.treeShell, 'Select a source method to render its call tree.');
                await searchSymbols();
              });

              elements.refreshTree.addEventListener('click', loadTree);
              elements.maxDepth.addEventListener('change', loadTree);

              for (const button of document.querySelectorAll('[data-view]')) {
                button.addEventListener('click', () => {
                  state.selectedView = button.dataset.view;
                  for (const peer of document.querySelectorAll('[data-view]')) {
                    peer.classList.toggle('active', peer === button);
                  }
                  loadTree();
                });
              }
            }

            async function main() {
              bindEvents();
              try {
                await loadConfig();
                await loadSnapshots();
                await searchSymbols();
              } catch (error) {
                setError(elements.symbolResults, error);
              }
            }

            main();
          </script>
        </body>
        </html>
        """;
}
