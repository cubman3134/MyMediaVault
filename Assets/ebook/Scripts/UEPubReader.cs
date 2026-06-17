using System.Collections.Generic;
using System.Xml;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System;
using UnityEngine;

namespace UEPub
{
    /// <summary>A single table-of-contents entry: a display title and the spine file it points to.</summary>
    public class UEPubTocEntry
    {
        public string title;
        public string href;   // file part only (no '#fragment')
        public int depth;     // nesting level (0 = top)
    }

    /// <summary>
    /// Minimal EPUB (2 and 3) reader: unzips the archive, follows container.xml -> OPF,
    /// and exposes the spine documents (chapters) as raw XHTML strings plus the book metadata.
    /// Parsing is defensive: a malformed or partial book yields an empty <see cref="chapters"/>
    /// list rather than throwing.
    /// </summary>
    public class UEPubReader
    {
        public string epubFolderLocation { get; private set; }
        public string htmlRoot { get; private set; }
        public UEPubMetadata metadata { get; private set; }

        public List<string> chapters { get; private set; }
        // Spine href (file part, decoded) parallel to <see cref="chapters"/>, for resolving TOC links.
        public List<string> chapterHrefs { get; private set; }
        // Table of contents (from the EPUB3 nav doc, or the EPUB2 NCX). Empty if neither is present.
        public List<UEPubTocEntry> toc { get; private set; }
        // File name of the navigation document, if it is itself a spine page (so the reader can make
        // that page interactive). Null otherwise.
        public string navHref { get; private set; }

        private readonly Dictionary<string, string> bookItems = new Dictionary<string, string>();
        private readonly List<string> spine = new List<string>();

        private string opfFileString;

        public UEPubReader(string file)
        {
            chapters = new List<string>();
            chapterHrefs = new List<string>();
            toc = new List<UEPubTocEntry>();
            metadata = new UEPubMetadata();

            if (string.IsNullOrEmpty(file) || !file.ToLowerInvariant().EndsWith(".epub"))
            {
                Debug.LogErrorFormat("The file '{0}' is not a .epub file", file);
                return;
            }

            if (!File.Exists(file))
            {
                Debug.LogErrorFormat("Epub file not found: '{0}'", file);
                return;
            }

            var folderName = Path.GetFileNameWithoutExtension(file);
            epubFolderLocation = Path.Combine(Application.temporaryCachePath, folderName);

            try
            {
                ZipUtil.Unzip(file, epubFolderLocation);
                ParseContainer();
                ParseOPF();
            }
            catch (Exception e)
            {
                Debug.LogErrorFormat("Failed to read epub '{0}': {1}", file, e);
            }
        }

        private void ParseContainer()
        {
            var containerFile = Path.Combine(epubFolderLocation, "META-INF", "container.xml");

            XmlDocument doc = new XmlDocument();
            doc.Load(containerFile);

            var xmlnsManager = new XmlNamespaceManager(doc.NameTable);
            xmlnsManager.AddNamespace("ns", "urn:oasis:names:tc:opendocument:xmlns:container");

            XmlNodeList rootFiles = doc.SelectNodes("/ns:container/ns:rootfiles/ns:rootfile", xmlnsManager);
            if (rootFiles == null || rootFiles.Count == 0)
            {
                throw new InvalidDataException("container.xml has no rootfile entry.");
            }
            opfFileString = rootFiles[0].Attributes["full-path"].InnerText;
        }

        private void ParseOPF()
        {
            var opfFile = Path.Combine(epubFolderLocation, opfFileString);

            XmlDocument doc = new XmlDocument();
            doc.Load(opfFile);

            var xmlnsManager = new XmlNamespaceManager(doc.NameTable);
            xmlnsManager.AddNamespace("ns", "http://www.idpf.org/2007/opf");
            xmlnsManager.AddNamespace("dc", "http://purl.org/dc/elements/1.1/");
            xmlnsManager.AddNamespace("dcterms", "http://purl.org/dc/terms/");

            ReadMetadata(doc, xmlnsManager);
            ReadChapters(opfFile, doc, xmlnsManager);
            ReadToc(doc, xmlnsManager);
        }

        private void ReadChapters(string opfFile, XmlDocument doc, XmlNamespaceManager xmlnsManager)
        {
            var nodes = doc.SelectNodes("/ns:package/ns:manifest/ns:item", xmlnsManager);
            foreach (XmlNode node in nodes)
            {
                var id = node.Attributes["id"];
                var href = node.Attributes["href"];
                if (id != null && href != null)
                {
                    bookItems[id.InnerText] = href.InnerText;
                }
            }

            nodes = doc.SelectNodes("/ns:package/ns:spine/ns:itemref", xmlnsManager);
            foreach (XmlNode node in nodes)
            {
                var idref = node.Attributes["idref"];
                if (idref != null)
                {
                    spine.Add(idref.InnerText);
                }
            }

            htmlRoot = Path.GetDirectoryName(opfFile) + Path.DirectorySeparatorChar;

            foreach (string idref in spine)
            {
                if (!bookItems.TryGetValue(idref, out var href))
                {
                    continue;
                }

                // hrefs may be URL-encoded (e.g. "chapter%201.xhtml") and contain sub-paths.
                var relativePath = Uri.UnescapeDataString(href);
                var fullPath = Path.GetFullPath(Path.Combine(htmlRoot, relativePath));

                if (!File.Exists(fullPath))
                {
                    Debug.LogWarningFormat("Epub chapter file missing, skipping: {0}", fullPath);
                    continue;
                }

                try
                {
                    chapters.Add(File.ReadAllText(fullPath));
                    chapterHrefs.Add(Path.GetFileName(relativePath));
                }
                catch (Exception e)
                {
                    Debug.LogWarningFormat("Could not read epub chapter '{0}': {1}", fullPath, e.Message);
                }
            }
        }

        /// <summary>
        /// Builds <see cref="toc"/> from the EPUB3 navigation document (manifest item with
        /// properties="nav"), falling back to the EPUB2 NCX. Failures leave the toc empty.
        /// </summary>
        private void ReadToc(XmlDocument doc, XmlNamespaceManager xmlnsManager)
        {
            try
            {
                // EPUB3 nav document.
                var items = doc.SelectNodes("/ns:package/ns:manifest/ns:item", xmlnsManager);
                foreach (XmlNode item in items)
                {
                    var props = item.Attributes["properties"];
                    var href = item.Attributes["href"];
                    if (props != null && href != null &&
                        Regex.IsMatch(props.InnerText, @"(^|\s)nav(\s|$)"))
                    {
                        navHref = Path.GetFileName(Uri.UnescapeDataString(href.InnerText));
                        ParseNavDocument(href.InnerText);
                        if (toc.Count > 0)
                        {
                            return;
                        }
                    }
                }

                // EPUB2 NCX fallback (referenced by the spine's toc attribute, or by media-type).
                string ncxId = null;
                var spineNode = doc.SelectSingleNode("/ns:package/ns:spine", xmlnsManager);
                if (spineNode?.Attributes?["toc"] != null)
                {
                    ncxId = spineNode.Attributes["toc"].InnerText;
                }
                string ncxHref = null;
                if (ncxId != null && bookItems.TryGetValue(ncxId, out var h))
                {
                    ncxHref = h;
                }
                else
                {
                    foreach (XmlNode item in items)
                    {
                        var mt = item.Attributes["media-type"];
                        if (mt != null && mt.InnerText == "application/x-dtbncx+xml")
                        {
                            ncxHref = item.Attributes["href"]?.InnerText;
                            break;
                        }
                    }
                }
                if (ncxHref != null)
                {
                    ParseNcx(ncxHref);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarningFormat("Could not read epub table of contents: {0}", e.Message);
            }
        }

        private void ParseNavDocument(string navHref)
        {
            var path = Path.GetFullPath(Path.Combine(htmlRoot, Uri.UnescapeDataString(navHref)));
            if (!File.Exists(path))
            {
                return;
            }
            var text = File.ReadAllText(path);

            // Prefer the toc nav element; fall back to the whole document.
            var navMatch = Regex.Match(text, @"<nav\b[^>]*epub:type\s*=\s*""[^""]*\btoc\b[^""]*""[^>]*>(.*?)</nav>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var scope = navMatch.Success ? navMatch.Groups[1].Value : text;

            foreach (Match m in Regex.Matches(scope, @"<a\b[^>]*\bhref\s*=\s*""([^""]+)""[^>]*>(.*?)</a>",
                         RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                AddTocEntry(m.Groups[2].Value, m.Groups[1].Value);
            }
        }

        private void ParseNcx(string ncxHref)
        {
            var path = Path.GetFullPath(Path.Combine(htmlRoot, Uri.UnescapeDataString(ncxHref)));
            if (!File.Exists(path))
            {
                return;
            }
            var text = File.ReadAllText(path);

            foreach (Match m in Regex.Matches(text,
                         @"<navLabel\b[^>]*>\s*<text\b[^>]*>(.*?)</text>\s*</navLabel>\s*<content\b[^>]*\bsrc\s*=\s*""([^""]+)""",
                         RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                AddTocEntry(m.Groups[1].Value, m.Groups[2].Value);
            }
        }

        private void AddTocEntry(string rawTitle, string rawHref)
        {
            var title = System.Net.WebUtility.HtmlDecode(Regex.Replace(rawTitle, "<[^>]+>", string.Empty)).Trim();
            title = Regex.Replace(title, @"\s+", " ");
            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(rawHref))
            {
                return;
            }
            // Keep only the file name (drop any '#fragment' and directory).
            var hrefFile = Path.GetFileName(Uri.UnescapeDataString(rawHref).Split('#')[0]);
            toc.Add(new UEPubTocEntry { title = title, href = hrefFile, depth = 0 });
        }

        private void ReadMetadata(XmlDocument doc, XmlNamespaceManager xmlnsManager)
        {
            metadata = new UEPubMetadata();

            // "Required" by the spec, but treat as optional so a non-conforming book still opens.
            SetMetadataProperty(ref metadata.title, "/ns:package/ns:metadata/dc:title", doc, xmlnsManager);
            SetMetadataProperty(ref metadata.language, "/ns:package/ns:metadata/dc:language", doc, xmlnsManager);
            SetMetadataProperty(ref metadata.identifier, "/ns:package/ns:metadata/dc:identifier", doc, xmlnsManager);

            // Optional props
            SetMetadataProperty(ref metadata.creator, "/ns:package/ns:metadata/dc:creator", doc, xmlnsManager);
            SetMetadataProperty(ref metadata.contributor, "/ns:package/ns:metadata/dc:contributor", doc, xmlnsManager);
            SetMetadataProperty(ref metadata.coverage, "/ns:package/ns:metadata/dc:coverage", doc, xmlnsManager);
            SetMetadataProperty(ref metadata.description, "/ns:package/ns:metadata/dc:description", doc, xmlnsManager);
            SetMetadataProperty(ref metadata.format, "/ns:package/ns:metadata/dc:format", doc, xmlnsManager);
            SetMetadataProperty(ref metadata.publisher, "/ns:package/ns:metadata/dc:publisher", doc, xmlnsManager);
            SetMetadataProperty(ref metadata.relation, "/ns:package/ns:metadata/dc:relation", doc, xmlnsManager);
            SetMetadataProperty(ref metadata.rights, "/ns:package/ns:metadata/dc:rights", doc, xmlnsManager);
            SetMetadataProperty(ref metadata.source, "/ns:package/ns:metadata/dc:source", doc, xmlnsManager);
            SetMetadataProperty(ref metadata.subject, "/ns:package/ns:metadata/dc:subject", doc, xmlnsManager);
            SetMetadataProperty(ref metadata.type, "/ns:package/ns:metadata/dc:type", doc, xmlnsManager);

            var node = doc.SelectSingleNode("/ns:package/ns:metadata/dc:date", xmlnsManager);
            if (node != null && DateTime.TryParse(node.InnerText,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var parsedDate))
            {
                metadata.date = parsedDate;
            }
        }

        private void SetMetadataProperty(ref string property, string XPath, XmlDocument doc, XmlNamespaceManager xmlnsManager)
        {
            var node = doc.SelectSingleNode(XPath, xmlnsManager);
            if (node != null)
            {
                property = node.InnerText;
            }
        }
    }
}
