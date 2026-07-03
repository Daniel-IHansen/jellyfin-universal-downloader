export default function (view, params) {

    // ── Lucide icon loading ──
    // Plugin pages are HTML fragments injected via innerHTML (both the Dashboard page loader
    // and the non-admin sidebar modal in injection.js), so a static <script> tag in the HTML
    // never executes. We load the Lucide UMD bundle imperatively instead and re-run
    // createIcons() after every render that introduces new [data-lucide] elements.
    function ensureLucide(cb) {
        if (window.lucide) { cb(); return; }
        if (window.__awLucideCbs) { window.__awLucideCbs.push(cb); return; }
        window.__awLucideCbs = [cb];
        var s = document.createElement('script');
        s.src = 'https://unpkg.com/lucide@latest';
        s.onload = function () {
            var cbs = window.__awLucideCbs || [];
            window.__awLucideCbs = null;
            cbs.forEach(function (fn) { fn(); });
        };
        s.onerror = function () { window.__awLucideCbs = null; };
        document.head.appendChild(s);
    }

    function icons() {
        ensureLucide(function () {
            try { window.lucide.createIcons(); } catch (e) { /* ignore */ }
        });
    }

    function esc(str) {
        if (!str) return '';
        var d = document.createElement('div');
        d.textContent = str;
        return d.innerHTML;
    }

    // Escape a string for safe inclusion inside a JS single-quoted string in an HTML attribute.
    // Prevents XSS via crafted provider names or titles breaking out of onclick="...fn('HERE')".
    function escJs(str) {
        if (!str) return '';
        return str.replace(/\\/g, '\\\\').replace(/'/g, "\\'").replace(/"/g, '&quot;');
    }

    function formatSize(bytes) {
        if (!bytes || bytes === 0) return '0 B';
        if (bytes < 1024) return bytes + ' B';
        if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
        if (bytes < 1024 * 1024 * 1024) return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
        var gb = bytes / (1024 * 1024 * 1024);
        if (gb < 1024) return gb.toFixed(2) + ' GB';
        return (gb / 1024).toFixed(2) + ' TB';
    }

    function formatCount(n) {
        if (n == null) return '0';
        if (n < 1000) return String(n);
        if (n < 1000000) return (n / 1000).toFixed(1).replace(/\.0$/, '') + 'k';
        return (n / 1000000).toFixed(1).replace(/\.0$/, '') + 'M';
    }

    function formatDate(isoStr) {
        if (!isoStr) return '—';
        try {
            var d = new Date(isoStr);
            var now = new Date();
            var diffMs = now - d;
            var diffMins = Math.floor(diffMs / 60000);

            if (diffMins < 1) return 'just now';
            if (diffMins < 60) return diffMins + 'm ago';
            var diffHrs = Math.floor(diffMins / 60);
            if (diffHrs < 24) return diffHrs + 'h ago';
            var diffDays = Math.floor(diffHrs / 24);
            if (diffDays < 7) return diffDays + 'd ago';

            return d.toLocaleDateString();
        } catch (e) {
            return isoStr;
        }
    }

    // English Sub/Dub only, everywhere. Language keys are canonical: "sub" / "dub".
    var LANG_NAMES = { sub: 'English Sub', dub: 'English Dub' };

    // Which languages each known source supports (cosmetic — controls which <option>s show in
    // the season download-language picker). Unknown/new sources default to both.
    var SITE_LANGUAGES = {
        aniworld: ['sub'],
        sto: ['dub'],
        anikoto: ['sub', 'dub'],
        animenexus: ['sub', 'dub'],
        aniwatch: ['sub', 'dub'],
    };

    function langIcon(langKey) {
        return langKey === 'dub' ? 'mic' : 'captions';
    }

    // Build language label HTML with an icon (sub = captions, dub = mic)
    function langLabelHtml(langKey) {
        var name = LANG_NAMES[langKey] || langKey;
        return '<i class="aw-icon" data-lucide="' + langIcon(langKey) + '" style="width:1.1em;height:1.1em;vertical-align:middle;margin-right:0.4em"></i>' + esc(name);
    }

    // Build <option> HTML for the season language picker (plain text only — <option> can't
    // render SVG icons in any browser).
    function getLangOptionsHtml(source) {
        var langs = SITE_LANGUAGES[source] || ['sub', 'dub'];
        var html = '<option value="">Use Settings Default</option>';
        langs.forEach(function (l) {
            html += '<option value="' + l + '">' + esc(LANG_NAMES[l]) + '</option>';
        });
        return html;
    }

    // Get site logo URL
    function siteLogoUrl(source) {
        return ApiClient.getUrl('AniBridge/SiteLogo/' + (source || 'aniworld'));
    }

    var AW = {
        currentSeriesTitle: null,
        currentSeriesUrl: null,
        currentSeriesSource: null,
        currentSeasonUrl: null,
        lastSearchQuery: null,
        lastSearchResults: null,
        downloadPollInterval: null,
        activeDownloadCount: 0,
        historyOffset: 0,
        historyStatusFilter: null,
        historySeriesFilter: null,
        seasonGeneration: 0,

        enabledSources: [],

        browseLoaded: { popular: false, new: false },

        // ── Tab switching ──
        switchTab: function (tab) {
            view.querySelectorAll('.aw-tab').forEach(function (t) { t.classList.remove('active'); });
            view.querySelector('[data-tab="' + tab + '"]').classList.add('active');
            view.querySelector('#searchTab').style.display = tab === 'search' ? '' : 'none';
            view.querySelector('#browseTab').style.display = tab === 'browse' ? '' : 'none';
            view.querySelector('#downloadsTab').style.display = tab === 'downloads' ? '' : 'none';
            view.querySelector('#historyTab').style.display = tab === 'history' ? '' : 'none';

            if (tab === 'downloads') {
                this.loadDownloads();
                this.startPolling();
            } else {
                this.stopPolling();
            }

            if (tab === 'history') {
                this.historyOffset = 0;
                this.loadStats();
                this.loadHistory(true);
            }

            if (tab === 'search' && this.currentSeriesUrl) {
                this.showSeries(encodeURIComponent(this.currentSeriesUrl), this.currentSeriesTitle, this.currentSeriesSource);
            }

            if (tab === 'browse' && !this.browseLoaded.popular) {
                this.loadBrowseSection('popular');
            }
        },

        // ── Browse ──
        switchBrowseSection: function (section, btn) {
            view.querySelectorAll('.aw-browse-pill').forEach(function (b) { b.classList.remove('active'); });
            if (btn) btn.classList.add('active');
            this.loadBrowseSection(section);
        },

        loadBrowseSection: function (section) {
            var container = view.querySelector('#aw-browse-content');
            if (!container) return;

            if (this.browseLoaded[section]) {
                this._renderBrowseCombined(section, container);
                return;
            }

            container.innerHTML = '<div class="aw-loading"><span class="aw-spinner"></span> Loading...</div>';

            var endpoint = section === 'new' ? 'AniBridge/New' : 'AniBridge/Popular';
            var enabled = this.enabledSources.filter(function (s) { return s.enabled; });

            var promises = enabled.map(function (s) {
                return ApiClient.fetch({
                    url: ApiClient.getUrl(endpoint, { source: s.source }),
                    type: 'GET', dataType: 'json'
                }).catch(function () { return []; });
            });

            Promise.all(promises).then(function (results) {
                AW.browseLoaded[section] = true;
                AW['browseCache_' + section] = enabled.map(function (s, idx) {
                    return { source: s.source, displayName: s.displayName, items: results[idx] || [] };
                });
                AW._renderBrowseCombined(section, container);
            }).catch(function (err) {
                container.innerHTML = '<div class="aw-empty"><div class="aw-empty-icon"><i class="aw-icon" data-lucide="x-circle"></i></div>Failed to load: ' + esc(err.message || 'Unknown error') + '</div>';
                icons();
            });
        },

        _renderBrowseCombined: function (section, container) {
            var groups = this['browseCache_' + section] || [];
            var html = '';
            var anyItems = false;

            groups.forEach(function (group) {
                if (group.items.length === 0) return;
                anyItems = true;
                html += '<div class="aw-browse-section-title">' + esc(group.displayName) + '</div>';
                html += AW._buildBrowseGrid(group.items, group.source);
            });

            if (!anyItems) {
                container.innerHTML = '<div class="aw-empty"><div class="aw-empty-icon"><i class="aw-icon" data-lucide="inbox"></i></div>No content found.</div>';
                icons();
                return;
            }

            container.innerHTML = html;
            icons();
        },

        _buildBrowseGrid: function (items, source) {
            var html = '<div class="aw-browse-grid">';
            items.forEach(function (item) {
                var itemSource = item.Source || source || 'aniworld';
                html += '<div class="aw-browse-card" onclick="window.AW.showSeries(\'' + encodeURIComponent(item.Url) + '\', \'' + escJs(item.Title) + '\', \'' + escJs(itemSource) + '\')">';
                html += '<img class="aw-browse-cover" src="' + esc(item.CoverImageUrl) + '" alt="' + esc(item.Title) + '" loading="lazy" onerror="this.style.display=\'none\'" />';
                html += '<img class="aw-browse-source-badge" src="' + siteLogoUrl(itemSource) + '" onerror="this.style.display=\'none\'" />';
                html += '<div class="aw-browse-info">';
                html += '<h3>' + esc(item.Title) + '</h3>';
                if (item.Genre) {
                    html += '<small>' + esc(item.Genre) + '</small>';
                }
                html += '</div></div>';
            });
            html += '</div>';
            return html;
        },

        // ── Search ──
        search: function () {
            var query = view.querySelector('#aw-search-input').value.trim();
            if (!query) return;

            this.lastSearchQuery = query;
            var content = view.querySelector('#aw-content');
            content.innerHTML = '<div class="aw-loading"><span class="aw-spinner"></span> Searching...</div>';

            ApiClient.fetch({
                url: ApiClient.getUrl('AniBridge/Search', { query: query }),
                type: 'GET',
                dataType: 'json'
            }).then(function (results) {
                AW.lastSearchResults = results;
                AW.renderSearchResults(results);
            }).catch(function (err) {
                content.innerHTML = '<div class="aw-empty"><div class="aw-empty-icon"><i class="aw-icon" data-lucide="x-circle"></i></div>Search failed: ' + esc(err.message || 'Unknown error') + '</div>';
                icons();
            });
        },

        searchGeneration: 0,

        renderSearchResults: function (results) {
            var content = view.querySelector('#aw-content');
            if (!results || results.length === 0) {
                content.innerHTML = '<div class="aw-empty"><div class="aw-empty-icon"><i class="aw-icon" data-lucide="search-x"></i></div>No results found. Try different keywords.</div>';
                icons();
                return;
            }

            this.searchGeneration++;
            var myGen = this.searchGeneration;

            var html = '<div class="aw-browse-grid">';
            results.forEach(function (item, idx) {
                var source = item.Source || 'aniworld';
                var cardId = 'aw-sr-' + idx;
                html += '<div class="aw-browse-card" id="' + cardId + '" onclick="window.AW.showSeries(\'' + encodeURIComponent(item.Url) + '\', \'' + escJs(item.Title) + '\', \'' + escJs(source) + '\')">';
                html += '<div class="aw-browse-cover aw-cover-placeholder" id="' + cardId + '-cover"></div>';
                html += '<img class="aw-browse-source-badge" src="' + siteLogoUrl(source) + '" onerror="this.style.display=\'none\'" />';
                html += '<div class="aw-browse-info">';
                html += '<h3>' + esc(item.Title) + '</h3>';
                html += '</div></div>';
            });
            html += '</div>';
            content.innerHTML = html;

            // Lazy-load all covers in parallel (no stagger)
            results.forEach(function (item, idx) {
                AW.fetchSearchCover(item.Url, item.Source || 'aniworld', 'aw-sr-' + idx, myGen);
            });
        },

        fetchSearchCover: function (seriesUrl, source, cardId, generation) {
            ApiClient.fetch({
                url: ApiClient.getUrl('AniBridge/Series', { url: seriesUrl, source: source }),
                type: 'GET',
                dataType: 'json'
            }).then(function (series) {
                if (generation !== undefined && AW.searchGeneration !== generation) return;
                var coverEl = view.querySelector('#' + cardId + '-cover');
                if (!coverEl) return;
                if (series.CoverImageUrl) {
                    var img = document.createElement('img');
                    img.className = 'aw-browse-cover';
                    img.src = series.CoverImageUrl;
                    img.alt = series.Title || '';
                    img.loading = 'lazy';
                    img.onerror = function () { this.style.display = 'none'; };
                    coverEl.parentNode.replaceChild(img, coverEl);
                }
            }).catch(function () { /* keep placeholder */ });
        },

        // ── Series Detail ──
        showSeries: function (encodedUrl, title, source) {
            var url = decodeURIComponent(encodedUrl);
            this.currentSeriesUrl = url;
            this.currentSeriesSource = source || 'aniworld';

            // If called from browse tab, switch to search tab to show the detail view
            var browseTab = view.querySelector('#browseTab');
            if (browseTab && browseTab.style.display !== 'none') {
                this.browseReturnTo = true;
                view.querySelectorAll('.aw-tab').forEach(function (t) { t.classList.remove('active'); });
                view.querySelector('[data-tab="search"]').classList.add('active');
                view.querySelector('#searchTab').style.display = '';
                view.querySelector('#browseTab').style.display = 'none';
            }

            var content = view.querySelector('#aw-content');
            content.innerHTML = '<div class="aw-loading"><span class="aw-spinner"></span> Loading series info...</div>';

            ApiClient.fetch({
                url: ApiClient.getUrl('AniBridge/Series', { url: url, source: this.currentSeriesSource }),
                type: 'GET',
                dataType: 'json'
            }).then(function (series) {
                AW.currentSeriesTitle = series.Title || title || 'Unknown';
                AW.renderSeries(series, url);
            }).catch(function (err) {
                content.innerHTML = '<div class="aw-empty"><div class="aw-empty-icon"><i class="aw-icon" data-lucide="x-circle"></i></div>Failed to load series: ' + esc(err.message || 'Unknown error') + '</div>';
                icons();
            });
        },

        renderSeries: function (series, seriesUrl) {
            var content = view.querySelector('#aw-content');
            var source = this.currentSeriesSource || 'aniworld';
            var html = '<button class="aw-btn aw-btn-secondary aw-back" onclick="window.AW.goBack()"><i class="aw-icon" data-lucide="arrow-left"></i> Back to Results</button>';

            if (source === 'sto' && series.Genres && series.Genres.some(function (g) { return g.toLowerCase() === 'anime'; })) {
                html += '<div class="aw-warning"><i class="aw-icon" data-lucide="triangle-alert"></i> For anime, using AniWorld as source is recommended.</div>';
            }

            html += '<div class="aw-series">';
            if (series.CoverImageUrl) {
                html += '<img class="aw-cover" src="' + esc(series.CoverImageUrl) + '" alt="Cover" onerror="this.style.display=\'none\'" />';
            }
            html += '<div class="aw-meta">';
            html += '<h2><img class="aw-source-logo" src="' + siteLogoUrl(source) + '" onerror="this.style.display=\'none\'" style="height:1.3em"> ' + esc(series.Title) + '</h2>';

            if (series.Genres && series.Genres.length > 0) {
                html += '<div class="aw-genres">';
                series.Genres.forEach(function (g) {
                    html += '<span class="aw-genre">' + esc(g) + '</span>';
                });
                html += '</div>';
            }

            if (series.Description) {
                var desc = series.Description;
                if (desc.length > 300) {
                    html += '<p>' + esc(desc.substring(0, 300)) + '...</p>';
                } else {
                    html += '<p>' + esc(desc) + '</p>';
                }
            }
            html += '</div></div>';

            if (series.Seasons && series.Seasons.length > 0) {
                html += '<div class="aw-series-actions">';
                html += '<div class="aw-seasons">';
                series.Seasons.forEach(function (season, idx) {
                    var cls = idx === 0 ? ' active' : '';
                    var seasonLabel = 'Season ' + season.Number;
                    html += '<button class="aw-season' + cls + '" data-url="' + esc(season.Url) + '" onclick="window.AW.loadSeason(\'' + encodeURIComponent(season.Url) + '\', this)">' + seasonLabel + '</button>';
                });
                if (series.HasMovies) {
                    var movieUrl = seriesUrl + '/filme';
                    html += '<button class="aw-season" data-url="' + esc(movieUrl) + '" onclick="window.AW.loadSeason(\'' + encodeURIComponent(movieUrl) + '\', this)"><i class="aw-icon" data-lucide="clapperboard"></i> Movies</button>';
                }
                html += '</div>';
                html += '</div>';
            }

            html += '<div id="aw-season-bar"></div>';
            html += '<div id="aw-episodes"></div>';
            content.innerHTML = html;
            icons();

            if (series.Seasons && series.Seasons.length > 0) {
                AW.loadSeason(encodeURIComponent(series.Seasons[0].Url));
            }
        },

        // ── Season Episodes ──
        loadSeason: function (encodedUrl, btn) {
            if (btn) {
                view.querySelectorAll('.aw-season').forEach(function (b) { b.classList.remove('active'); });
                btn.classList.add('active');
            }

            this.seasonGeneration++;
            var myGeneration = this.seasonGeneration;

            var url = decodeURIComponent(encodedUrl);
            this.currentSeasonUrl = url;
            var epContainer = view.querySelector('#aw-episodes');
            var barContainer = view.querySelector('#aw-season-bar');
            if (!epContainer) return;

            epContainer.innerHTML = '<div class="aw-loading"><span class="aw-spinner"></span> Loading episodes...</div>';
            if (barContainer) barContainer.innerHTML = '';

            var source = this.currentSeriesSource || 'aniworld';
            ApiClient.fetch({
                url: ApiClient.getUrl('AniBridge/Episodes', { url: url, source: source }),
                type: 'GET',
                dataType: 'json'
            }).then(function (episodes) {
                if (AW.seasonGeneration !== myGeneration) return;
                AW.renderEpisodes(episodes, url, myGeneration);
            }).catch(function (err) {
                if (AW.seasonGeneration !== myGeneration) return;
                epContainer.innerHTML = '<div class="aw-empty">Failed to load episodes: ' + esc(err.message || '') + '</div>';
            });
        },

        renderEpisodes: function (episodes, seasonUrl, generation) {
            var epContainer = view.querySelector('#aw-episodes');
            var barContainer = view.querySelector('#aw-season-bar');
            var source = this.currentSeriesSource || 'aniworld';

            if (!episodes || episodes.length === 0) {
                epContainer.innerHTML = '<div class="aw-empty"><div class="aw-empty-icon"><i class="aw-icon" data-lucide="inbox"></i></div>No episodes found.</div>';
                if (barContainer) barContainer.innerHTML = '';
                icons();
                return;
            }

            if (barContainer) {
                var bar = '<div class="aw-season-actions">';
                bar += '<span class="aw-ep-count">' + episodes.length + ' episode' + (episodes.length === 1 ? '' : 's') + '</span>';
                bar += '<select id="aw-season-lang" class="aw-lang-select" title="Language for downloads">';
                bar += getLangOptionsHtml(source);
                bar += '</select>';
                bar += '<label style="display:inline-flex;align-items:center;gap:0.3em;font-size:0.82em;cursor:pointer;opacity:0.85" title="Priority downloads are added to the front of the queue"><input type="checkbox" id="aw-priority-cb" style="cursor:pointer"> Priority</label>';
                bar += '<label style="display:inline-flex;align-items:center;gap:0.3em;font-size:0.82em;cursor:pointer;opacity:0.85" title="Redownload episodes even if they are already flagged as downloaded"><input type="checkbox" id="aw-force-cb" style="cursor:pointer"> Force</label>';
                bar += '<button class="aw-btn aw-btn-success aw-btn-sm" onclick="window.AW.downloadSeason(\'' + encodeURIComponent(seasonUrl) + '\')"><i class="aw-icon" data-lucide="download"></i> Download Season</button>';
                if (AW.currentSeriesUrl) {
                    bar += '<button class="aw-btn aw-btn-all-seasons aw-btn-sm" onclick="window.AW.downloadAllSeasons(\'' + encodeURIComponent(AW.currentSeriesUrl) + '\')"><i class="aw-icon" data-lucide="download"></i> Download All Seasons</button>';
                }
                bar += '</div>';

                barContainer.innerHTML = bar;
            }

            var html = '<div class="aw-episodes">';
            episodes.forEach(function (ep) {
                var label = ep.IsMovie ? 'Movie ' + ep.Number : ep.Number;
                var epId = 'ep-' + ep.Number + '-' + (ep.IsMovie ? 'movie' : 'ep');
                html += '<div class="aw-ep" id="' + epId + '">';
                html += '<span class="aw-ep-num">' + label + '</span>';
                html += '<span class="aw-ep-title" id="' + epId + '-title">Loading...</span>';
                html += '<span class="aw-ep-downloaded" id="' + epId + '-dl" style="display:none"></span>';
                html += '<div class="aw-ep-actions">';
                html += '<button class="aw-btn aw-btn-primary aw-btn-sm" onclick="window.AW.downloadEpisode(\'' + encodeURIComponent(ep.Url) + '\')"><i class="aw-icon" data-lucide="download"></i> Download</button>';
                html += '<button class="aw-btn aw-btn-secondary aw-btn-sm" onclick="window.AW.toggleProviders(\'' + encodeURIComponent(ep.Url) + '\', \'' + epId + '\')">Providers</button>';
                html += '</div>';
                html += '</div>';
                html += '<div id="' + epId + '-providers" class="aw-ep-providers" style="display:none"></div>';
            });
            html += '</div>';
            epContainer.innerHTML = html;
            icons();

            var myGen = generation || AW.seasonGeneration;
            episodes.forEach(function (ep, idx) {
                var epId = 'ep-' + ep.Number + '-' + (ep.IsMovie ? 'movie' : 'ep');
                setTimeout(function () {
                    if (AW.seasonGeneration !== myGen) return;
                    AW.fetchEpisodeTitle(ep.Url, epId, myGen);
                    AW.checkIsDownloaded(ep.Url, epId);
                }, idx * 150);
            });
        },

        fetchEpisodeTitle: function (url, epId, generation) {
            var titleEl = view.querySelector('#' + epId + '-title');
            if (!titleEl) return;

            var source = this.currentSeriesSource || 'aniworld';
            ApiClient.fetch({
                url: ApiClient.getUrl('AniBridge/Episode', { url: url, source: source }),
                type: 'GET',
                dataType: 'json'
            }).then(function (details) {
                if (generation !== undefined && AW.seasonGeneration !== generation) return;
                titleEl = view.querySelector('#' + epId + '-title');
                if (!titleEl) return;
                var title = details.TitleEn || '';
                titleEl.textContent = title || '—';
            }).catch(function () {
                if (generation !== undefined && AW.seasonGeneration !== generation) return;
                titleEl = view.querySelector('#' + epId + '-title');
                if (titleEl) titleEl.textContent = '—';
            });
        },

        checkIsDownloaded: function (url, epId) {
            ApiClient.fetch({
                url: ApiClient.getUrl('AniBridge/IsDownloaded', { url: url, title: this.currentSeriesTitle || 'Unknown' }),
                type: 'GET',
                dataType: 'json'
            }).then(function (result) {
                if (result && result.downloaded && result.languages && result.languages.length > 0) {
                    var badge = view.querySelector('#' + epId + '-dl');
                    if (badge) {
                        var html = '<i class="aw-icon" data-lucide="check"></i> ';
                        for (var i = 0; i < result.languages.length; i++) {
                            html += '<i class="aw-icon" data-lucide="' + langIcon(result.languages[i]) + '" style="width:1.1em;height:1.1em;vertical-align:middle;margin-right:0.2em" title="' + esc(LANG_NAMES[result.languages[i]] || result.languages[i]) + '"></i>';
                        }
                        badge.innerHTML = html;
                        badge.style.display = '';
                        icons();
                    }
                }
            }).catch(function () { /* ignore */ });
        },

        // ── Providers ──
        toggleProviders: function (encodedUrl, epId) {
            var panel = view.querySelector('#' + epId + '-providers');
            if (!panel) return;

            if (panel.style.display !== 'none') {
                panel.style.display = 'none';
                return;
            }

            panel.style.display = '';
            panel.innerHTML = '<div class="aw-loading"><span class="aw-spinner"></span> Loading...</div>';

            var url = decodeURIComponent(encodedUrl);
            var source = this.currentSeriesSource || 'aniworld';
            ApiClient.fetch({
                url: ApiClient.getUrl('AniBridge/Episode', { url: url, source: source }),
                type: 'GET',
                dataType: 'json'
            }).then(function (details) {
                var html = '';

                var hasAny = false;
                for (var langKey in details.ProvidersByLanguage) {
                    hasAny = true;
                    html += '<div class="aw-lang-group">';
                    html += '<div class="aw-lang-label">' + langLabelHtml(langKey) + '</div>';
                    html += '<div class="aw-provider-btns">';
                    var providers = details.ProvidersByLanguage[langKey];
                    for (var prov in providers) {
                        html += '<button class="aw-btn aw-btn-secondary aw-btn-sm" onclick="window.AW.downloadWithOptions(\'' + encodeURIComponent(url) + '\', \'' + escJs(langKey) + '\', \'' + escJs(prov) + '\')">' + esc(prov) + '</button>';
                    }
                    html += '</div></div>';
                }

                if (!hasAny) {
                    html = '<div style="opacity:0.5;padding:0.5em">No providers available for this episode.</div>';
                }

                panel.innerHTML = html;
                icons();
            }).catch(function () {
                panel.innerHTML = '<div style="color:#ef5350;padding:0.5em">Failed to load providers.</div>';
            });
        },

        // ── Downloads ──
        _isPriorityChecked: function () {
            var cb = view.querySelector('#aw-priority-cb');
            return cb ? cb.checked : false;
        },

        _isForceChecked: function () {
            var cb = view.querySelector('#aw-force-cb');
            return cb ? cb.checked : false;
        },

        _checkMaintenance: function () {
            if (this.maintenanceMode) {
                Dashboard.alert('Downloads are blocked: maintenance mode is active.');
                return true;
            }
            return false;
        },

        downloadEpisode: function (encodedUrl) {
            if (this._checkMaintenance()) return;
            var url = decodeURIComponent(encodedUrl);
            var langSelect = view.querySelector('#aw-season-lang');
            var lang = (langSelect && langSelect.value) ? langSelect.value : null;
            this._startDownload(url, lang, null);
        },

        downloadWithOptions: function (encodedUrl, langKey, provider) {
            if (this._checkMaintenance()) return;
            var url = decodeURIComponent(encodedUrl);
            this._startDownload(url, langKey, provider);
        },

        downloadSeason: function (encodedSeasonUrl) {
            if (this._checkMaintenance()) return;
            var seasonUrl = decodeURIComponent(encodedSeasonUrl);

            var body = {
                SeasonUrl: seasonUrl,
                SeriesTitle: this.currentSeriesTitle,
                Source: this.currentSeriesSource || 'aniworld'
            };

            if (this._isPriorityChecked()) body.Priority = true;
            if (this._isForceChecked()) body.Force = true;

            var langSelect = view.querySelector('#aw-season-lang');
            if (langSelect && langSelect.value) {
                body.LanguageKey = langSelect.value;
            }

            ApiClient.fetch({
                url: ApiClient.getUrl('AniBridge/DownloadSeason'),
                type: 'POST',
                data: JSON.stringify(body),
                contentType: 'application/json',
                dataType: 'json'
            }).then(function (tasks) {
                var count = tasks ? tasks.length : 0;
                if (count > 0) {
                    Dashboard.alert('Queued ' + count + ' episode(s) for download!');
                    AW.switchTab('downloads');
                } else {
                    Dashboard.alert('All episodes already downloaded or no episodes found.');
                }
            }).catch(function (err) {
                AW._handleApiError(err, 'Batch download failed');
            });
        },

        downloadAllSeasons: function (encodedSeriesUrl) {
            if (this._checkMaintenance()) return;
            var seriesUrl = decodeURIComponent(encodedSeriesUrl);

            var body = {
                SeriesUrl: seriesUrl,
                SeriesTitle: this.currentSeriesTitle,
                Source: this.currentSeriesSource || 'aniworld'
            };

            if (this._isPriorityChecked()) body.Priority = true;
            if (this._isForceChecked()) body.Force = true;

            var langSelect = view.querySelector('#aw-season-lang');
            if (langSelect && langSelect.value) {
                body.LanguageKey = langSelect.value;
            }

            ApiClient.fetch({
                url: ApiClient.getUrl('AniBridge/DownloadAll'),
                type: 'POST',
                data: JSON.stringify(body),
                contentType: 'application/json',
                dataType: 'json'
            }).then(function (result) {
                var msg = 'Queued ' + result.queued + ' episode(s) across ' + result.seasons + ' season(s)!';
                if (result.skipped > 0) {
                    msg += ' (' + result.skipped + ' already downloaded)';
                }
                if (result.queued > 0) {
                    Dashboard.alert(msg);
                    AW.switchTab('downloads');
                } else {
                    Dashboard.alert('All episodes already downloaded!');
                }
            }).catch(function (err) {
                AW._handleApiError(err, 'Download all failed');
            });
        },

        _startDownload: function (episodeUrl, langKey, provider) {
            var body = {
                EpisodeUrl: episodeUrl,
                SeriesTitle: this.currentSeriesTitle,
                Source: this.currentSeriesSource || 'aniworld'
            };
            if (langKey) body.LanguageKey = langKey;
            if (provider) body.Provider = provider;
            if (this._isPriorityChecked()) body.Priority = true;
            if (this._isForceChecked()) body.Force = true;

            ApiClient.fetch({
                url: ApiClient.getUrl('AniBridge/Download'),
                type: 'POST',
                data: JSON.stringify(body),
                contentType: 'application/json',
                dataType: 'json'
            }).then(function (task) {
                Dashboard.alert('Download started: ' + (task.EpisodeTitle || task.OutputPath || task.Id));
                AW.updateBadge(AW.activeDownloadCount + 1);
            }).catch(function (err) {
                AW._handleApiError(err, 'Download failed');
            });
        },

        _handleApiError: function (err, prefix) {
            if (err && typeof err.json === 'function') {
                err.json().then(function (body) {
                    var msg = body.detail || body.title || body.error || JSON.stringify(body);
                    Dashboard.alert(prefix + ': ' + msg);
                }).catch(function () {
                    Dashboard.alert(prefix + ': HTTP ' + (err.status || 'error'));
                });
            } else {
                Dashboard.alert(prefix + ': ' + (err.message || 'Unknown error'));
            }
        },

        // ── Downloads Tab ──
        loadDownloads: function () {
            ApiClient.fetch({
                url: ApiClient.getUrl('AniBridge/Downloads'),
                type: 'GET',
                dataType: 'json'
            }).then(function (downloads) {
                AW.renderDownloads(downloads);
            }).catch(function () {
                var container = view.querySelector('#aw-downloads');
                if (container) container.innerHTML = '<div class="aw-empty">Failed to load downloads.</div>';
            });
        },

        renderDownloads: function (downloads) {
            var container = view.querySelector('#aw-downloads');
            if (!container) return;

            var active = 0;
            if (downloads) {
                downloads.forEach(function (dl) {
                    if (['Queued', 'Resolving', 'Extracting', 'Downloading', 'Retrying'].indexOf(dl.Status) !== -1) {
                        active++;
                    }
                });
            }
            AW.activeDownloadCount = active;
            AW.updateBadge(active);

            if (!downloads || downloads.length === 0) {
                container.innerHTML = '<div class="aw-empty"><div class="aw-empty-icon"><i class="aw-icon" data-lucide="inbox"></i></div>No active downloads.<br>Search for anime and start downloading!</div>';
                icons();
                return;
            }

            var statusOrder = { Downloading: 0, Retrying: 0, Extracting: 0, Resolving: 0, Queued: 1, Completed: 2, Failed: 2, Cancelled: 2 };
            downloads.sort(function (a, b) {
                var sa = statusOrder[a.Status] !== undefined ? statusOrder[a.Status] : 9;
                var sb = statusOrder[b.Status] !== undefined ? statusOrder[b.Status] : 9;
                if (sa !== sb) return sa - sb;
                if (sa === 2) {
                    // Done group: newest first
                    return (b.StartedAt || '').localeCompare(a.StartedAt || '');
                }
                // Active (0) and Queued (1): preserve backend insertion order (next-up first)
                return 0;
            });

            var hasCompleted = downloads.some(function (dl) {
                return ['Completed', 'Failed', 'Cancelled'].indexOf(dl.Status) !== -1;
            });

            var html = '';
            if (hasCompleted) {
                html += '<div class="aw-dl-actions"><button class="aw-btn aw-btn-secondary aw-btn-sm" onclick="window.AW.clearCompleted()"><i class="aw-icon" data-lucide="trash-2"></i> Clear Completed</button></div>';
            }

            html += '<div class="aw-dl">';
            downloads.forEach(function (dl) {
                var statusCls = 'aw-status-' + dl.Status.toLowerCase();
                var isFailed = dl.Status === 'Failed';
                var isActive = ['Queued', 'Resolving', 'Extracting', 'Downloading', 'Retrying'].indexOf(dl.Status) !== -1;
                var fileName = dl.OutputPath ? dl.OutputPath.split('/').pop().split('\\').pop() : dl.Id;
                var dlSource = dl.Source || 'aniworld';

                html += '<div class="aw-dl-item">';
                html += '<div class="aw-dl-info">';
                html += '<strong><img class="aw-source-logo" src="' + siteLogoUrl(dlSource) + '" onerror="this.style.display=\'none\'" style="height:1em"> ' + esc(dl.EpisodeTitle || fileName) + '</strong>';
                var langLabel = LANG_NAMES[dl.Language] || dl.Language || '';
                var metaParts = [esc(dl.Provider)];
                if (langLabel) metaParts.push(esc(langLabel));
                if (dl.Username) metaParts.push(esc(dl.Username));
                html += '<small>' + metaParts.join(' · ');
                if (dl.RetryCount > 0) {
                    html += ' (retry ' + dl.RetryCount + '/' + dl.MaxRetries + ')';
                }
                if (dl.FileSizeBytes > 0) {
                    html += '<span class="aw-dl-size">' + formatSize(dl.FileSizeBytes) + '</span>';
                }
                html += '</small>';
                if (dl.Error && dl.Status !== 'Retrying') {
                    html += '<div class="aw-dl-error">' + esc(dl.Error) + '</div>';
                }
                if (dl.Status === 'Retrying' && dl.Error) {
                    html += '<div class="aw-dl-retry-info"><i class="aw-icon" data-lucide="clock" style="width:1em;height:1em;vertical-align:middle"></i> ' + esc(dl.Error) + '</div>';
                }
                if (dl.LanguageFallbackNote) {
                    html += '<div class="aw-dl-retry-info">' + esc(dl.LanguageFallbackNote) + '</div>';
                }
                html += '</div>';

                html += '<div class="aw-dl-progress"><div class="aw-dl-bar" style="width:' + dl.Progress + '%"></div></div>';
                html += '<span class="aw-dl-pct">' + dl.Progress + '%</span>';
                html += '<span class="aw-status ' + statusCls + '">' + esc(dl.Status) + '</span>';

                html += '<div class="aw-dl-btns">';
                if (isActive) {
                    html += '<button class="aw-btn aw-btn-danger aw-btn-sm" onclick="window.AW.cancelDownload(\'' + dl.Id + '\')" title="Cancel"><i class="aw-icon" data-lucide="x" style="width:1em;height:1em"></i></button>';
                }
                if (isFailed) {
                    html += '<button class="aw-btn aw-btn-warning aw-btn-sm" onclick="window.AW.retryDownload(\'' + dl.Id + '\')" title="Retry"><i class="aw-icon" data-lucide="refresh-cw" style="width:1em;height:1em"></i></button>';
                }
                html += '</div>';

                html += '</div>';
            });
            html += '</div>';

            container.innerHTML = html;
            icons();
        },

        cancelDownload: function (id) {
            ApiClient.fetch({
                url: ApiClient.getUrl('AniBridge/Downloads/' + id),
                type: 'DELETE'
            }).then(function () {
                AW.loadDownloads();
            });
        },

        retryDownload: function (id) {
            ApiClient.fetch({
                url: ApiClient.getUrl('AniBridge/Downloads/' + id + '/Retry'),
                type: 'POST'
            }).then(function () {
                Dashboard.alert('Retrying download...');
                AW.loadDownloads();
            }).catch(function (err) {
                Dashboard.alert('Retry failed: ' + (err.message || 'Unknown error'));
            });
        },

        clearCompleted: function () {
            ApiClient.fetch({
                url: ApiClient.getUrl('AniBridge/Downloads/Clear'),
                type: 'POST'
            }).then(function () {
                AW.loadDownloads();
            });
        },

        updateBadge: function (count) {
            var badge = view.querySelector('#aw-dl-badge');
            if (badge) {
                if (count > 0) {
                    badge.textContent = count;
                    badge.style.display = '';
                } else {
                    badge.style.display = 'none';
                }
            }
        },

        startPolling: function () {
            this.stopPolling();
            this.downloadPollInterval = setInterval(function () {
                AW.loadDownloads();
            }, 2500);
        },

        stopPolling: function () {
            if (this.downloadPollInterval) {
                clearInterval(this.downloadPollInterval);
                this.downloadPollInterval = null;
            }
        },

        // ── History Tab ──
        loadStats: function () {
            ApiClient.fetch({
                url: ApiClient.getUrl('AniBridge/Stats'),
                type: 'GET',
                dataType: 'json'
            }).then(function (stats) {
                AW.renderStats(stats);
            }).catch(function () {
                var container = view.querySelector('#aw-history-stats');
                if (container) container.innerHTML = '';
            });
        },

        renderStats: function (stats) {
            var container = view.querySelector('#aw-history-stats');
            if (!container) return;

            var html = '<div class="aw-stats">';
            html += '<div class="aw-stat"><div class="aw-stat-value">' + formatCount(stats.TotalDownloads) + '</div><div class="aw-stat-label">Total Downloads</div></div>';
            html += '<div class="aw-stat"><div class="aw-stat-value green">' + formatCount(stats.Completed) + '</div><div class="aw-stat-label">Completed</div></div>';
            html += '<div class="aw-stat"><div class="aw-stat-value red">' + formatCount(stats.Failed) + '</div><div class="aw-stat-label">Failed</div></div>';
            html += '<div class="aw-stat"><div class="aw-stat-value">' + formatSize(stats.TotalBytes) + '</div><div class="aw-stat-label">Total Size</div></div>';
            html += '<div class="aw-stat"><div class="aw-stat-value orange">' + formatCount(stats.UniqueSeriesCount) + '</div><div class="aw-stat-label">Series</div></div>';
            html += '</div>';
            container.innerHTML = html;
        },

        loadHistory: function (reset) {
            if (reset) {
                this.historyOffset = 0;
            }

            var params = { limit: 30, offset: this.historyOffset };
            if (this.historyStatusFilter) params.status = this.historyStatusFilter;
            if (this.historySeriesFilter) params.series = this.historySeriesFilter;

            ApiClient.fetch({
                url: ApiClient.getUrl('AniBridge/History', params),
                type: 'GET',
                dataType: 'json'
            }).then(function (records) {
                AW.renderHistory(records, reset);
                AW.renderHistoryFilters();
            }).catch(function () {
                var container = view.querySelector('#aw-history');
                if (container) container.innerHTML = '<div class="aw-empty">Failed to load history.</div>';
            });
        },

        renderHistoryFilters: function () {
            var container = view.querySelector('#aw-history-filters-container');
            if (!container) return;

            if (container.dataset.rendered === 'true') return;
            container.dataset.rendered = 'true';

            var html = '<div class="aw-hist-filters">';
            html += '<select id="aw-hist-status" onchange="window.AW.filterHistory()">';
            html += '<option value="">All Status</option>';
            html += '<option value="Completed">Completed</option>';
            html += '<option value="Failed">Failed</option>';
            html += '<option value="Cancelled">Cancelled</option>';
            html += '</select>';
            html += '<input type="text" id="aw-hist-series" placeholder="Filter by series..." style="padding:0.4em 0.7em;background:rgba(255,255,255,0.06);border:1px solid rgba(255,255,255,0.12);border-radius:6px;color:inherit;font-size:0.85em;min-width:200px;" />';
            html += '<button class="aw-btn aw-btn-secondary aw-btn-sm" onclick="window.AW.filterHistory()">Filter</button>';
            html += '</div>';
            container.innerHTML = html;
        },

        filterHistory: function () {
            var statusEl = view.querySelector('#aw-hist-status');
            var seriesEl = view.querySelector('#aw-hist-series');
            this.historyStatusFilter = statusEl ? statusEl.value || null : null;
            this.historySeriesFilter = seriesEl ? seriesEl.value.trim() || null : null;
            this.loadHistory(true);
        },

        renderHistory: function (records, reset) {
            var container = view.querySelector('#aw-history');
            if (!container) return;

            if ((!records || records.length === 0) && reset) {
                container.innerHTML = '<div class="aw-empty"><div class="aw-empty-icon"><i class="aw-icon" data-lucide="inbox"></i></div>No download history yet.<br>Downloaded episodes will appear here.</div>';
                icons();
                return;
            }

            var html = reset ? '<div class="aw-history">' : '';

            records.forEach(function (rec) {
                var statusCls = 'aw-status-' + rec.Status.toLowerCase();
                var title = rec.EpisodeTitle || '';
                var seLabel = 'S' + String(rec.Season).padStart(2, '0') + 'E' + String(rec.Episode).padStart(2, '0');
                var recSource = rec.Source || 'aniworld';

                html += '<div class="aw-hist-item">';
                html += '<div class="aw-hist-info">';
                html += '<strong><img class="aw-source-logo" src="' + siteLogoUrl(recSource) + '" onerror="this.style.display=\'none\'" style="height:1em"> ' + esc(rec.SeriesTitle) + ' ' + seLabel;
                if (title) html += ' - ' + esc(title);
                html += '</strong>';
                html += '<small>' + esc(rec.Provider) + ' · ' + esc(LANG_NAMES[rec.Language] || rec.Language);
                if (rec.Error) html += ' · ' + esc(rec.Error.substring(0, 60));
                html += '</small>';
                html += '</div>';
                html += '<div class="aw-hist-meta">';
                if (rec.FileSizeBytes > 0) {
                    html += '<span class="aw-hist-size">' + formatSize(rec.FileSizeBytes) + '</span>';
                }
                html += '<span class="aw-status ' + statusCls + '">' + esc(rec.Status) + '</span>';
                html += '<span class="aw-hist-date">' + formatDate(rec.StartedAt) + '</span>';
                html += '</div>';
                html += '</div>';
            });

            if (reset) {
                html += '</div>';
                container.innerHTML = html;
            } else {
                var histDiv = container.querySelector('.aw-history');
                if (histDiv) {
                    histDiv.insertAdjacentHTML('beforeend', html);
                }
            }

            var moreContainer = container.querySelector('.aw-hist-more');
            if (moreContainer) moreContainer.remove();

            if (records && records.length >= 30) {
                AW.historyOffset += records.length;
                container.insertAdjacentHTML('beforeend', '<div class="aw-hist-more"><button class="aw-btn aw-btn-secondary" onclick="window.AW.loadHistory(false)">Load More</button></div>');
            }

            icons();
        },

        goBack: function () {
            this.currentSeriesUrl = null;
            this.currentSeriesSource = null;
            if (this.browseReturnTo) {
                this.switchTab('browse');
                this.browseReturnTo = null;
            } else if (this.lastSearchResults) {
                this.renderSearchResults(this.lastSearchResults);
            } else if (this.lastSearchQuery) {
                view.querySelector('#aw-search-input').value = this.lastSearchQuery;
                this.search();
            } else {
                view.querySelector('#aw-content').innerHTML = '';
            }
        }
    };

    // Expose globally for onclick handlers in dynamic HTML
    window.AW = AW;

    // Load enabled sources + maintenance mode from server
    ApiClient.fetch({
        url: ApiClient.getUrl('AniBridge/EnabledSources'),
        type: 'GET',
        dataType: 'json'
    }).then(function (result) {
        AW.enabledSources = result.sources || [];
        AW.maintenanceMode = result.maintenanceMode === true;
        if (AW.maintenanceMode) {
            var banner = view.querySelector('#aw-maintenance-banner');
            var text = view.querySelector('#aw-maintenance-text');
            if (banner && text) {
                text.textContent = result.maintenanceMessage || 'The downloader is currently under maintenance.';
                banner.style.display = '';
            }
        }
    }).catch(function () { /* ignore */ });

    // Hide settings button when opened from sidebar (non-admin view)
    if (params && params.sidebar) {
        var settingsBtn = view.querySelector('#aw-settings-btn');
        if (settingsBtn) {
            settingsBtn.style.display = 'none';
        }
    }

    // Bind Enter key to search
    var searchInput = view.querySelector('#aw-search-input');
    if (searchInput) {
        searchInput.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') {
                e.preventDefault();
                AW.search();
            }
        });
    }

    icons();

    // Poll badge count periodically
    var badgePollInterval = setInterval(function () {
        ApiClient.fetch({
            url: ApiClient.getUrl('AniBridge/Downloads'),
            type: 'GET',
            dataType: 'json'
        }).then(function (downloads) {
            var active = 0;
            if (downloads) {
                downloads.forEach(function (dl) {
                    if (['Queued', 'Resolving', 'Extracting', 'Downloading', 'Retrying'].indexOf(dl.Status) !== -1) {
                        active++;
                    }
                });
            }
            AW.updateBadge(active);
        }).catch(function () { /* ignore */ });
    }, 10000);

    // Cleanup when navigating away
    view.addEventListener('viewhide', function () {
        AW.stopPolling();
        if (badgePollInterval) {
            clearInterval(badgePollInterval);
            badgePollInterval = null;
        }
    });
}
