var PurrNetDataRegionLib = {
    PurrNetRegion_TimeZone: function () {
        var zone = '';
        try { zone = Intl.DateTimeFormat().resolvedOptions().timeZone || ''; } catch (e) { }
        var size = lengthBytesUTF8(zone) + 1;
        var buffer = _malloc(size);
        stringToUTF8(zone, buffer, size);
        return buffer;
    },

    PurrNetRegion_Language: function () {
        var language = '';
        try { language = navigator.language || ''; } catch (e) { }
        var size = lengthBytesUTF8(language) + 1;
        var buffer = _malloc(size);
        stringToUTF8(language, buffer, size);
        return buffer;
    }
};

mergeInto(LibraryManager.library, PurrNetDataRegionLib);
