# LLM情感分析功能

## 概述

LLM情感分析功能通过大语言模型分析VPet的说话内容，自动选择并显示与情感匹配的表情包图片。

## 功能特性

- **自动情感分析**: 捕获VPet说话内容，使用LLM分析情感关键词
- **向量检索匹配**: 使用向量相似度匹配最合适的表情包
- **智能缓存**: 两级缓存系统（内存+持久化），减少LLM请求
- **多LLM支持**: 支持OpenAI、Gemini、Ollama（后两者待实现）
- **降级策略**: LLM失败时自动降级到VPet心情模式

## 架构组件

### 核心接口

- **ILLMClient**: LLM客户端接口，提供情感分析和向量嵌入
- **IEmotionAnalyzer**: 情感分析器接口
- **IVectorRetriever**: 向量检索器接口

### 实现类

- **OpenAIClient**: OpenAI API客户端实现
- **EmotionAnalyzer**: 情感分析器，集成缓存和限流
- **VectorRetriever**: 向量检索器，基于余弦相似度匹配
- **CacheManager**: 两级缓存管理器
- **SpeechCapturer**: 语音捕获器，监听VPet说话事件
- **ImageSelector**: 图片选择器，选择并显示匹配图片

## 使用方法

### 1. 配置LLM提供商

在插件设置中配置：

#### OpenAI
- **API Key**: 您的OpenAI API密钥
- **Base URL**: `https://api.openai.com/v1` (默认)
- **模型**: gpt-3.5-turbo (情感分析) + text-embedding-3-small (向量嵌入)
  
#### Gemini
- **API Key**: 您的Google Gemini API密钥
- **Base URL**: `https://generativelanguage.googleapis.com/v1beta` (默认)
- **模型**: gemini-pro (情感分析) + embedding-001 (向量嵌入)
  
#### Ollama (本地部署)
- **Base URL**: 本地Ollama服务地址 (默认 `http://localhost:11434`)
- **Model**: 模型名称 (默认 `llama2`，可选 `mistral`、`llama3` 等)
- **注意**: 需要先在本地安装并运行Ollama服务

### 2. 创建标签文件

在表情包目录下创建 `label.txt` 文件，格式如下：

```
# 注释行以#开头
文件名.png: 标签1, 标签2, 标签3

# 示例
happy_1.png: happy, joyful, excited, 开心, 快乐
sad_1.png: sad, unhappy, depressed, 伤心, 难过
angry_1.png: angry, mad, furious, 生气, 愤怒
```

支持的标签文件位置：
- `VPet_Expression/label.txt` - 内置表情包标签
- `DIY_Expression/label.txt` - DIY表情包标签

### 3. 启用功能

在插件设置中：
1. 选择LLM提供商
2. 输入API密钥
3. 勾选"启用LLM情感分析"
4. 保存设置

## 工作流程

1. **语音捕获**: VPet说话时，`SpeechCapturer` 捕获文本
2. **情感分析**: `EmotionAnalyzer` 检查缓存，如未命中则调用LLM分析
3. **向量检索**: `VectorRetriever` 计算情感与标签的相似度
4. **图片选择**: `ImageSelector` 从Top 3中随机选择一张图片
5. **显示图片**: 调用 `ImageMgr.DisplayImage()` 显示图片

## 性能优化

### 缓存策略

- **内存缓存**: 最多100条，LRU淘汰，<1ms查询
- **持久化缓存**: 最多1000条，基于使用频率淘汰
- **懒保存**: 每5分钟自动保存一次

### 限流策略

- 最小请求间隔: 10秒
- 超过限制时使用降级策略

### 向量预计算

- 启动时预计算所有标签的向量嵌入
- 避免运行时重复计算

## 降级策略

当LLM不可用时，自动降级到VPet心情模式：

- Happy → happy, joyful
- Normal → calm, neutral
- PoorCondition → tired, weary
- Ill → sick, unwell

## 调试模式

启用调试模式后，会在控制台输出详细日志：

- LLM请求和响应
- 缓存命中/未命中
- 向量相似度分数
- 图片选择决策

## 文件结构

```
EmotionAnalysis/
├── ILLMClient.cs              # LLM客户端接口
├── IEmotionAnalyzer.cs        # 情感分析器接口
├── IVectorRetriever.cs        # 向量检索器接口
├── EmotionAnalysisSettings.cs # 配置类
├── LLMClient/
│   └── OpenAIClient.cs        # OpenAI客户端实现
├── EmotionAnalyzer.cs         # 情感分析器实现
├── VectorRetriever.cs         # 向量检索器实现
├── CacheManager.cs            # 缓存管理器
├── SpeechCapturer.cs          # 语音捕获器
├── ImageSelector.cs           # 图片选择器
└── README.md                  # 本文档
```

## 常见问题

### Q: 为什么没有显示匹配的表情包？

A: 检查以下几点：
1. 是否创建了 `label.txt` 文件
2. 标签文件格式是否正确
3. 图片文件名是否与标签文件中的一致
4. 是否启用了调试模式查看日志

### Q: LLM请求失败怎么办？

A: 系统会自动降级到VPet心情模式，不影响基本功能。检查：
1. API密钥是否正确
2. 网络连接是否正常
3. API配额是否充足

### Q: 如何减少LLM请求次数？

A: 系统已内置缓存和限流机制：
- 相同文本会命中缓存
- 10秒内最多请求一次
- 缓存会持久化到磁盘

## 未来计划

- [ ] 实现语义相似度缓存
- [ ] 支持自定义LLM提示词
- [ ] 添加情感分析准确率统计
- [ ] 支持更多LLM提供商（Claude、文心一言等）
