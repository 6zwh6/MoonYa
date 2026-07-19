"""MoonYa 搜索 HTTP API 服务器"""
import asyncio
import json
import os
import sys
import traceback
from aiohttp import web

# 确保能 import web_search 包（向上两级到 MoonYa-Python/）
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from web_search import web_search, web_fetch


class SearchServer:
    def __init__(self, host="0.0.0.0", port=58902):
        self.host = host
        self.port = port
        self.app = web.Application()
        self.app.router.add_post("/search", self.handle_search)
        self.app.router.add_get("/health", self.handle_health)

    async def handle_health(self, request: web.Request) -> web.Response:
        return web.json_response({"status": "ok"})

    async def handle_search(self, request: web.Request) -> web.Response:
        try:
            body = await request.json()
        except Exception:
            return web.json_response({"error": "请求体不是有效 JSON"}, status=400)

        action = body.get("action", "").strip()
        if not action:
            return web.json_response({"error": "缺少 action 参数"}, status=400)

        if action == "web_search":
            query = body.get("query", "").strip()
            if not query:
                return web.json_response({"error": "缺少 query 参数"}, status=400)
            try:
                loop = asyncio.get_running_loop()
                result = await loop.run_in_executor(None, web_search, query)
                return web.json_response(json.loads(result))
            except Exception as e:
                traceback.print_exc()
                return web.json_response({"error": f"搜索失败: {str(e)}"}, status=500)

        elif action == "web_fetch":
            url = body.get("url", "").strip()
            if not url:
                return web.json_response({"error": "缺少 url 参数"}, status=400)
            try:
                loop = asyncio.get_running_loop()
                result = await loop.run_in_executor(None, web_fetch, url)
                return web.json_response(json.loads(result))
            except Exception as e:
                traceback.print_exc()
                return web.json_response({"error": f"抓取失败: {str(e)}"}, status=500)

        else:
            return web.json_response(
                {"error": f"未知 action: {action}，支持: web_search, web_fetch"},
                status=400,
            )


def main():
    host = sys.argv[1] if len(sys.argv) > 1 else "0.0.0.0"
    port = int(sys.argv[2]) if len(sys.argv) > 2 else 58902
    server = SearchServer(host, port)
    print(f"MoonYa 搜索服务启动: http://{host}:{port}")
    web.run_app(server.app, host=host, port=port, print=None)


if __name__ == "__main__":
    main()
