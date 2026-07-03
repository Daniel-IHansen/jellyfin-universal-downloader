export default function (view, params) {
    var pluginId = '7c1a9e3d-5b2f-4a68-9d3e-2f6b8c4a1e70';

    // Finds (or creates) the Sites[] entry for a source, mirroring PluginConfiguration.GetSiteConfig
    // on the server so the UI always has something to bind to even for a site added after this
    // config was first saved.
    function getSiteConfig(config, source) {
        if (!config.Sites) config.Sites = [];
        for (var i = 0; i < config.Sites.length; i++) {
            if (config.Sites[i].Source && config.Sites[i].Source.toLowerCase() === source) {
                return config.Sites[i].Config || (config.Sites[i].Config = {});
            }
        }
        var entry = { Source: source, Config: {} };
        config.Sites.push(entry);
        return entry.Config;
    }

    function loadConfig() {
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            // General
            view.querySelector('#txtMaxDownloads').value = config.MaxConcurrentDownloads || 2;
            view.querySelector('#txtMaxRetries').value = config.MaxRetries != null ? config.MaxRetries : 3;
            view.querySelector('#chkAutoScan').checked = config.AutoScanLibrary !== false;
            view.querySelector('#chkNonAdminAccess').checked = config.EnableNonAdminAccess === true;
            view.querySelector('#chkMaintenanceMode').checked = config.MaintenanceMode === true;
            view.querySelector('#txtMaintenanceMessage').value = config.MaintenanceMessage || '';
            view.querySelector('#txtProxyUrl').value = config.ProxyUrl || '';
            view.querySelector('#txtMoviePath').value = config.MovieDownloadPath || '';
            view.querySelector('#chkLanguageFallback').checked = config.EnableLanguageFallback !== false;

            // AniWorld (English Sub only)
            var aw = getSiteConfig(config, 'aniworld');
            view.querySelector('#chkAniworldEnabled').checked = aw.Enabled !== false;
            view.querySelector('#txtAniworldPathSub').value = aw.DownloadPathSub || aw.DownloadPath || '';
            view.querySelector('#selAniworldProvider').value = aw.PreferredProvider || 'Vidmoly';
            view.querySelector('#selAniworldFallback').value = aw.FallbackProvider || '';

            // s.to (English Dub only)
            var sto = getSiteConfig(config, 'sto');
            view.querySelector('#chkStoEnabled').checked = sto.Enabled === true;
            view.querySelector('#txtStoBaseUrl').value = sto.CustomBaseUrl || '';
            view.querySelector('#txtStoPathDub').value = sto.DownloadPathDub || sto.DownloadPath || '';
            view.querySelector('#selStoProvider').value = sto.PreferredProvider || 'VOE';
            view.querySelector('#selStoFallback').value = sto.FallbackProvider || '';

            // Anikoto (Sub + Dub)
            var anikoto = getSiteConfig(config, 'anikoto');
            view.querySelector('#chkAnikotoEnabled').checked = anikoto.Enabled !== false;
            view.querySelector('#txtAnikotoPathSub').value = anikoto.DownloadPathSub || anikoto.DownloadPath || '';
            view.querySelector('#txtAnikotoPathDub').value = anikoto.DownloadPathDub || '';
            view.querySelector('#selAnikotoLanguage').value = anikoto.PreferredLanguage || 'sub';

            // Anime Nexus (Sub + Dub, experimental)
            var animenexus = getSiteConfig(config, 'animenexus');
            view.querySelector('#chkAnimenexusEnabled').checked = animenexus.Enabled === true;
            view.querySelector('#txtAnimenexusPathSub').value = animenexus.DownloadPathSub || animenexus.DownloadPath || '';
            view.querySelector('#txtAnimenexusPathDub').value = animenexus.DownloadPathDub || '';
            view.querySelector('#selAnimenexusLanguage').value = animenexus.PreferredLanguage || 'sub';

            Dashboard.hideLoadingMsg();
        });
    }

    function saveConfig() {
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            // General
            config.MaxConcurrentDownloads = parseInt(view.querySelector('#txtMaxDownloads').value, 10) || 2;
            config.MaxRetries = parseInt(view.querySelector('#txtMaxRetries').value, 10) || 0;
            config.AutoScanLibrary = view.querySelector('#chkAutoScan').checked;
            config.EnableNonAdminAccess = view.querySelector('#chkNonAdminAccess').checked;
            config.MaintenanceMode = view.querySelector('#chkMaintenanceMode').checked;
            config.MaintenanceMessage = view.querySelector('#txtMaintenanceMessage').value.trim();
            config.ProxyUrl = view.querySelector('#txtProxyUrl').value.trim();
            config.MovieDownloadPath = view.querySelector('#txtMoviePath').value.trim();
            config.EnableLanguageFallback = view.querySelector('#chkLanguageFallback').checked;

            // AniWorld
            var aw = getSiteConfig(config, 'aniworld');
            aw.Enabled = view.querySelector('#chkAniworldEnabled').checked;
            aw.DownloadPathSub = view.querySelector('#txtAniworldPathSub').value.trim();
            aw.DownloadPath = aw.DownloadPathSub;
            aw.PreferredLanguage = 'sub';
            aw.PreferredProvider = view.querySelector('#selAniworldProvider').value;
            aw.FallbackProvider = view.querySelector('#selAniworldFallback').value;

            // s.to
            var sto = getSiteConfig(config, 'sto');
            sto.Enabled = view.querySelector('#chkStoEnabled').checked;
            sto.CustomBaseUrl = view.querySelector('#txtStoBaseUrl').value.trim();
            sto.DownloadPathDub = view.querySelector('#txtStoPathDub').value.trim();
            sto.DownloadPath = sto.DownloadPathDub;
            sto.PreferredLanguage = 'dub';
            sto.PreferredProvider = view.querySelector('#selStoProvider').value;
            sto.FallbackProvider = view.querySelector('#selStoFallback').value;

            // Anikoto
            var anikoto = getSiteConfig(config, 'anikoto');
            anikoto.Enabled = view.querySelector('#chkAnikotoEnabled').checked;
            anikoto.DownloadPathSub = view.querySelector('#txtAnikotoPathSub').value.trim();
            anikoto.DownloadPathDub = view.querySelector('#txtAnikotoPathDub').value.trim();
            anikoto.DownloadPath = anikoto.DownloadPathSub;
            anikoto.PreferredLanguage = view.querySelector('#selAnikotoLanguage').value;
            anikoto.PreferredProvider = 'Anikoto';

            // Anime Nexus
            var animenexus = getSiteConfig(config, 'animenexus');
            animenexus.Enabled = view.querySelector('#chkAnimenexusEnabled').checked;
            animenexus.DownloadPathSub = view.querySelector('#txtAnimenexusPathSub').value.trim();
            animenexus.DownloadPathDub = view.querySelector('#txtAnimenexusPathDub').value.trim();
            animenexus.DownloadPath = animenexus.DownloadPathSub;
            animenexus.PreferredLanguage = view.querySelector('#selAnimenexusLanguage').value;
            animenexus.PreferredProvider = 'AnimeNexus';

            ApiClient.updatePluginConfiguration(pluginId, config).then(function () {
                Dashboard.processPluginConfigurationUpdateResult();
            });
        });
    }

    view.addEventListener('viewshow', function () {
        loadConfig();
    });

    view.querySelector('#AniBridgeConfigForm').addEventListener('submit', function (e) {
        e.preventDefault();
        saveConfig();
        return false;
    });
}
