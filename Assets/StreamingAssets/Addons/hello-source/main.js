// Hello Source - a sample media-source addon.
//
// A media-source addon implements getCatalog() and, optionally, search(). Both receive a JSON string
// argument and must return a JSON string of the shape:
//   { "title": "...", "items": [ { "id", "title", "subtitle", "type", "url", "thumbnailUrl" }, ... ] }
//
// Host functions available (sandboxed, gated by manifest "permissions"):
//   log(message)                -> void
//   httpGet(url)                -> string   (requires "network" permission)
//   getStorage(key)             -> string
//   setStorage(key, value)      -> void

function getCatalog(argJson) {
    log("Hello Source: building catalog");

    var items = [
        {
            id: "pride-and-prejudice",
            title: "Pride and Prejudice",
            subtitle: "Jane Austen (via Hello Source addon)",
            type: "ebook",
            url: "Assets/ebook/Books/austen-pride-and-prejudice-illustrations.epub"
        }
    ];

    return JSON.stringify({ title: "Hello Source", items: items });
}

function search(argJson) {
    var query = (JSON.parse(argJson || "{}").query) || "";
    log("Hello Source: search for '" + query + "'");
    return JSON.stringify({ title: "Search: " + query, items: [] });
}
