import json
import urllib.request
import urllib.parse
import re
import html as html_mod
from . import register


@register(
    "web_search",
    "搜索互联网信息。用于查找最新新闻、事实或任何实时信息。",
    {"query": {"type": "string", "description": "搜索关键词"}}
)
def web_search(query: str) -> str:
    """Web search with fallback chain: DuckDuckGo → Baidu → Bing China."""
    first_error = None
    # 首选 DuckDuckGo
    try:
        data = urllib.parse.urlencode({"q": query}).encode()
        req = urllib.request.Request(
            "https://html.duckduckgo.com/html/",
            data=data,
            headers={
                "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
                              "AppleWebKit/537.36 (KHTML, like Gecko) "
                              "Chrome/120.0.0.0 Safari/537.36"
            }
        )
        with urllib.request.urlopen(req, timeout=15) as resp:
            html_content = resp.read().decode("utf-8", errors="ignore")

        results = []
        # DuckDuckGo HTML 版搜索结果结构：
        #   <div class="result">
        #     <h2 class="result__title">
        #       <a class="result__a" href="...">title</a>
        #     </h2>
        #     <a class="result__snippet">snippet</a>
        #   </div>
        for block in re.finditer(
            r'<div[^>]*class="result[^"]*"[^>]*>(.*?)</div>\s*(?:</div>)?',
            html_content, re.DOTALL
        ):
            block_html = block.group(1)

            # 提取标题 + 链接
            link_match = re.search(
                r'<a[^>]*class="result__a"[^>]*href="(https?://[^"]*)"[^>]*>(.*?)</a>',
                block_html, re.DOTALL
            )
            if not link_match:
                continue
            link_url = link_match.group(1)
            link_text = re.sub(r'<[^>]+>', '', link_match.group(2)).strip()

            # 提取摘要
            snippet_match = re.search(
                r'<a[^>]*class="result__snippet"[^>]*>(.*?)</a>',
                block_html, re.DOTALL
            )
            snippet = ""
            if snippet_match:
                snippet = re.sub(r'<[^>]+>', '', snippet_match.group(1)).strip()
                snippet = html_mod.unescape(snippet)

            results.append({
                "title": html_mod.unescape(link_text),
                "url": urllib.parse.unquote(link_url),
                "snippet": snippet
            })

        return json.dumps({"query": query, "results": results[:8]}, ensure_ascii=False)

    except Exception as e:
        first_error = e

    # Fallback 1: Baidu（国内最稳定）
    try:
        return _baidu_search(query)
    except Exception:
        pass

    # Fallback 2: Bing China
    try:
        return _bing_search_fallback(query)
    except Exception:
        pass

    return json.dumps({"error": f"Search failed: {str(first_error)}"})


def _baidu_search(query: str) -> str:
    """Fallback: Baidu HTML search (most reliable in Chinese network)."""
    encoded = urllib.parse.quote(query)
    url = f"https://www.baidu.com/s?wd={encoded}"
    req = urllib.request.Request(url, headers={
        "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
                      "AppleWebKit/537.36 (KHTML, like Gecko) "
                      "Chrome/120.0.0.0 Safari/537.36",
        "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
        "Accept-Language": "zh-CN,zh;q=0.9,en;q=0.8",
    })
    with urllib.request.urlopen(req, timeout=10) as resp:
        html_content = resp.read().decode("utf-8", errors="ignore")

    results = []
    # Baidu 搜索结果结构（2024+ 新版）：
    #   <div class="result" id="N">
    #     <div class="c-container">
    #       <h3 class="t"><a href="url">title</a></h3>
    #       <div class="c-abstract">snippet</div>
    #     </div>
    #   </div>
    #   老版也可能直接是 <div class="c-container">...</div>
    for block in re.finditer(
        r'<div[^>]*(?:class="(?:result|c-container)[^"]*"|id="\d+")[^>]*>(.*?)</div>\s*(?:</div>)?',
        html_content, re.DOTALL
    ):
        block_html = block.group(1)

        # 提取标题 + 链接 (h3 > a)
        link_match = re.search(
            r'<h3[^>]*>.*?<a[^>]*href="(https?://[^"]*)"[^>]*>(.*?)</a>',
            block_html, re.DOTALL
        )
        if not link_match:
            continue
        link_url = link_match.group(1)
        link_text = re.sub(r'<[^>]+>', '', link_match.group(2)).strip()
        if not link_text:
            continue

        # 提取摘要：尝试多种可能的容器
        snippet = ""
        snippet_match = re.search(
            r'<div[^>]*class="c-abstract"[^>]*>(.*?)</div>',
            block_html, re.DOTALL
        )
        if not snippet_match:
            snippet_match = re.search(
                r'<span[^>]*class="content-right[^"]*"[^>]*>(.*?)</span>',
                block_html, re.DOTALL
            )
        if not snippet_match:
            # 兜底：取 h3 后的第一个 div 文本
            snippet_match = re.search(
                r'</h3>\s*<div[^>]*>(.*?)</div>',
                block_html, re.DOTALL
            )
        if snippet_match:
            snippet = re.sub(r'<[^>]+>', '', snippet_match.group(1)).strip()
            snippet = html_mod.unescape(snippet)

        results.append({
            "title": html_mod.unescape(link_text),
            "url": urllib.parse.unquote(link_url),
            "snippet": snippet,
        })

    return json.dumps({"query": query, "results": results[:8]}, ensure_ascii=False)


def _bing_search_fallback(query: str) -> str:
    """Fallback: Bing China HTML search."""
    encoded = urllib.parse.quote(query)
    url = f"https://cn.bing.com/search?q={encoded}&count=10"
    req = urllib.request.Request(url, headers={
        "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
                      "AppleWebKit/537.36 (KHTML, like Gecko) "
                      "Chrome/120.0.0.0 Safari/537.36"
    })
    with urllib.request.urlopen(req, timeout=10) as resp:
        html_content = resp.read().decode("utf-8", errors="ignore")

    results = []
    for block in re.finditer(
        r'<li class="b_algo"[^>]*>(.*?)</li>',
        html_content, re.DOTALL
    ):
        block_html = block.group(1)
        h2_match = re.search(
            r'<h2[^>]*>.*?<a[^>]*href="(http[^"]*)"[^>]*>(.*?)</a>.*?</h2>',
            block_html, re.DOTALL
        )
        if h2_match:
            link_url = h2_match.group(1)
            link_text = h2_match.group(2)
        else:
            link_match = re.search(
                r'<a[^>]*href="(http[^"]*)"[^>]*>(.*?)</a>',
                block_html, re.DOTALL
            )
            if not link_match:
                continue
            link_url = link_match.group(1)
            link_text = link_match.group(2)
        snippet_match = re.search(
            r'<p class="b_lineclamp[^"]*"[^>]*>(.*?)</p>',
            block_html, re.DOTALL
        )
        snippet = ""
        if snippet_match:
            snippet = re.sub(r'<[^>]+>', '', snippet_match.group(1)).strip()
            snippet = html_mod.unescape(snippet)
        results.append({
            "title": html_mod.unescape(re.sub(r'<[^>]+>', '', link_text).strip()),
            "url": urllib.parse.unquote(link_url),
            "snippet": snippet
        })

    return json.dumps({"query": query, "results": results[:8]}, ensure_ascii=False)


@register(
    "web_fetch",
    "抓取并阅读网页内容。用于阅读文章、文档或任何网页。",
    {"url": {"type": "string", "description": "要抓取和阅读的网址"}}
)
def web_fetch(url: str) -> str:
    try:
        req = urllib.request.Request(url, headers={
            "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"
        })
        with urllib.request.urlopen(req, timeout=15) as resp:
            content = resp.read().decode("utf-8", errors="ignore")

        # Strip HTML tags for readability
        text = re.sub(r'<script[^>]*>.*?</script>', '', content, flags=re.DOTALL | re.I)
        text = re.sub(r'<style[^>]*>.*?</style>', '', text, flags=re.DOTALL | re.I)
        text = re.sub(r'<[^>]+>', ' ', text)
        text = re.sub(r'\s+', ' ', text).strip()
        text = html_mod.unescape(text)

        if len(text) > 6000:
            text = text[:6000] + "\n... [truncated]"

        return json.dumps({"url": url, "content": text}, ensure_ascii=False)
    except Exception as e:
        return json.dumps({"error": f"Fetch failed: {str(e)}"})
