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
	public Button NextPageButton;
	private UEPubReader _epub;
	private bool _bookFinished;
    private const char _tagStartChar = (char)0x01;
    private const char _tagEndChar = (char)0x02;
    private Stack<string> _currentTags = new Stack<string>();
    //private EpubBook epubBook;



    // Use this for initialization
    void Start () {
		NextPageButton.onClick.AddListener(TaskOnClick);

        OpenEbookFile ();
	}

	private string _chapterText;
	private int _currentPageStartIndex = 0;
	private int _charsBeingAddedToPage = 0;
	private int _currentChapter;

    void TaskOnClick()
    {
		RenderNextPage();
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

	public void RenderNextPage()
	{
		if (_bookFinished)
		{
			return;
		}
		int newStartPageIndex = _currentPageStartIndex + (displayText?.cachedTextGenerator?.characterCount ?? 0) - _charsBeingAddedToPage;
        int currentIndex = newStartPageIndex;
		displayText.text = string.Empty;
		char currentChar = '\0';
		string currentWord = string.Empty;
		List<char> spaceChars = new List<char>()
		{
			' ',
			'\n'
		};
		int truncateWordsOverThisManyChars = 6;
        while (displayText.cachedTextGenerator.characterCountVisible <= displayText.cachedTextGenerator.characterCount)
		{
			if (currentIndex >= _chapterText.Length)
			{
				break;
			}
			currentChar = _chapterText[currentIndex++];
            if (spaceChars.Contains(currentChar))
            {
				currentWord = string.Empty;
            }
			else if (currentChar == _tagStartChar)
			{
				currentIndex++;
				string currentTag = string.Empty;
				do
				{
					currentChar = _chapterText[currentIndex++];
					currentTag += currentChar;
				} while (currentChar != '>');
				_currentTags.Push(currentTag);
				continue;
			}
			else if (currentChar == _tagEndChar)
			{
				_currentTags.TryPop(out _);
			}
			else
			{
				currentWord += currentChar;
			}
			displayText.text += currentChar;
		}
        _currentPageStartIndex = newStartPageIndex;
        _charsBeingAddedToPage = 0;
		if (!spaceChars.Contains(currentChar))
		{
			if (currentWord.Length > truncateWordsOverThisManyChars)
			{
				displayText.text = displayText.text.Substring(0, displayText.text.Length - 2);
				displayText.text += "-";
				_charsBeingAddedToPage = 1;
            }
			else
			{
				displayText.text = displayText.text.Substring(0, displayText.text.Length - currentWord.Length);
			}
		}
		// done with chapter
		if (displayText.text == string.Empty)
		{
			if (++_currentChapter == _epub.chapters.Count)
			{
				_bookFinished = true;
				displayText.text = "End of book.";
				return;
			}
			_currentPageStartIndex = 0;
			PrepareTextForChapter(_currentChapter);
			RenderNextPage();
		}
    }

	public void PrepareEPub()
	{
        _epub = new UEPubReader("Assets/ebook/Books/austen-pride-and-prejudice-illustrations.epub");
    }

    public void PrepareTextForChapter(int chapterNumber)
	{
        var text = _epub.chapters[chapterNumber];
        text = text.Replace("-\n", "");
        text = text.Replace("\n", " ");
        text = text.Replace("<br>", "\n");
        text = text.Replace("<p>", "\n");
        text = text.Replace("</p>", "\n");
        var tagsToKeep = new List<string>()
        {
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
        text = text.Replace("{head}", $"{_tagStartChar}<size=120%>");
        text = text.Replace("{b}", $"{_tagStartChar}<b>");
        text = text.Replace("{em}", $"{_tagStartChar}<i>");

        /*text = text.Replace("{/head}", "</size>\n");
        text = text.Replace("{/title}", "</size>\n");
        text = text.Replace("{/b}", "</b>");
        text = text.Replace("{/em}", "</i>");*/
        text = text.Replace("{/head}", $"{_tagEndChar}\n");
        text = text.Replace("{/b}", $"{_tagEndChar}");
        text = text.Replace("{/em}", $"{_tagEndChar}");
        for (int hIterator = 1; hIterator <= 6; hIterator++)
        {
            var tag = "{h" + hIterator + "}";
            var endTag = "{/h" + hIterator + "}";
            int newSize = 100 + hIterator * 4;
            text = text.Replace(tag, $"<size={newSize}>");
			text = text.Replace(endTag, $"{_tagEndChar}");
			//text = text.Replace(endTag, $"</size>\n");
        }
        _chapterText = text;
    }

    void OpenEbookFile()
	{
		PrepareEPub();
		PrepareTextForChapter(10); // test todo todo
		RenderNextPage();
		//displayText.text = "This is some <b><size=50><color=#ff0000ff>Text</color></size></b>";
        //displayText.text = _chapterText;
		//File.WriteAllText("Assets/ebook/Books/austen-pride-and-prejudice-chapter-10.html", epub.chapters[10]);
        //displayText.text = epub.chapters[10];
    }
}
