using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UEPub;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Windows;
using static UnityEngine.InputSystem.InputControlScheme.MatchResult;

public enum Tags
{
	Unassigned,
	H1,
	H2,
	H3,
	H4,
	H5,
	H6,
	Paragraph,
	Head,
	Title,
	Emphasis,
	Bold,
	ListItem, // li, inside of an ol or ul tag
	UnorderedList, // ul bullet points rather than numbered
	OrderedList, // ol 1. 2. 3.
	Break
}

public class TaggedData
{
	public static Dictionary<string, Tags> TagNamesToTags { get; set; } = new Dictionary<string, Tags>()
	{
		{ "h1",		Tags.H1			},
		{ "h2",		Tags.H2			},
		{ "h3",		Tags.H3			},
		{ "h4",		Tags.H4			},
		{ "h5",		Tags.H5			},
		{ "h6",		Tags.H6			},
		{ "p",		Tags.Paragraph	},
		{ "head",	Tags.Head		},
		{ "title",	Tags.Title		},
		{ "em",		Tags.Emphasis	},
		{ "b",		Tags.Bold		},
		{ "li",		Tags.ListItem	},
		{ "ul",		Tags.UnorderedList	},
		{ "ol",		Tags.OrderedList	},
		{ "br",		Tags.Break		},
	};

	public Tags Tag { get; set; }

}

public class EbookRenderer : MonoBehaviour {
	public Text displayText;
	public TextAsset testBook;

	//private EpubBook epubBook;

	// Use this for initialization
	void Start () {
		OpenEbookFile ();
	}

	string GetTextWithTags(string text, string tag, bool keepInnerInfo)
	{
        System.Text.RegularExpressions.Match match = null;
        do
        {
            match = Regex.Match(text, $"<{tag}.*?>(.*?)<\\/{tag}>");
            if (!match.Success)
            {
                break;
            }
            var tagLength = match.Groups[1].Index - match.Groups[0].Index;
            var preText = text.Substring(0, match.Groups[0].Index);
            var innerText = text.Substring(match.Groups[1].Index, match.Groups[1].Length);
            var postText = text.Substring(match.Groups[0].Index + match.Groups[0].Length);
			var newText = preText;
			if (keepInnerInfo)
			{
				newText = newText + "{" + tag + "}" + innerText + "{/" + tag + "}";

            }
            text = newText + postText;
        }
        while (match.Success);
        return text;
	}
	
	void OpenEbookFile(){
		// Opening a book
		//epubBook = EpubReader.OpenBook("austen-pride-and-prejudice-illustrations.epub");
		//EpubChapter chapter = epubBook.Chapters [0];
		//displayText.text = chapter.HtmlContent;
		int standardFontSize = 10;
		var epub = new UEPubReader("Assets/ebook/Books/austen-pride-and-prejudice-illustrations.epub");
		var text = epub.chapters[10];
        text = text.Replace("<br>", "\n");
        text = text.Replace("<p>", "\n");
        text = text.Replace("</p>", "\n");
        var tagsToKeep = new List<string>()
		{
			//"p",
			"head",
			"b",
			"em",
			"h1",
			"h2",
			"h3",
			"h4",
			"h5",
			"h6"
		};
		var tagsToDeleteInnerInfo = new List<string>() 
		{ 
			"title" 
		};
		foreach (var tag in tagsToKeep)
		{
			text = GetTextWithTags(text, tag, true);
        }
		foreach (var tag in tagsToDeleteInnerInfo)
		{
			text = GetTextWithTags(text, tag, false);
		}
        text = Regex.Replace(text, "<.*?>", String.Empty);
		text = text.Replace("{head}", "<size=120%>");
		text = text.Replace("{/head}", "</size>\n");
		text = text.Replace("{title}", "<size=140%>");
		text = text.Replace("{/title}", "</size>\n");
		text = text.Replace("{b}", "<b>");
		text = text.Replace("{/b}", "</b>");
		text = text.Replace("{em}", "<em>");
		text = text.Replace("{/em}", "</em>");
		for (int hIterator = 1; hIterator <= 6; hIterator++)
		{
			var tag = "{h" + hIterator + "}";
			var endTag = "{/h" + hIterator + "}";
			int newSize = 100 + hIterator * 4;
			text = text.Replace(tag, $"<size={newSize}>");
			text = text.Replace(endTag, $"</size>\n");
		}
        /*var htmlStartTag = text.IndexOf("<html");
		text = text.Replace("<em>", "{emphasis}");
		text = text.Replace("</em>", "{emphasis/}");
		text = text.Replace("<b>", "{bold}");
		text = text.Replace("</b>", "{bold/}");*/
        Debug.Log (epub.epubFolderLocation);
		//displayText.text = "This is some <b><size=50><color=#ff0000ff>Text</color></size></b>";
        displayText.text = text;
		//File.WriteAllText("Assets/ebook/Books/austen-pride-and-prejudice-chapter-10.html", epub.chapters[10]);
        //displayText.text = epub.chapters[10];
    }
}
