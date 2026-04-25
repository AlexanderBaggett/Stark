/* Generated Raylib 5.5 ABI shim for Stark examples. */
#include "raylib.h"

void stark_raylib_SetWindowIcon(const Image *image)
{
    SetWindowIcon(*image);
}

void stark_raylib_GetMonitorPosition(Vector2 *result, int monitor)
{
    *result = GetMonitorPosition(monitor);
}

void stark_raylib_GetWindowPosition(Vector2 *result)
{
    *result = GetWindowPosition();
}

void stark_raylib_GetWindowScaleDPI(Vector2 *result)
{
    *result = GetWindowScaleDPI();
}

void stark_raylib_GetClipboardImage(Image *result)
{
    *result = GetClipboardImage();
}

void stark_raylib_ClearBackground(const Color *color)
{
    ClearBackground(*color);
}

void stark_raylib_BeginMode2D(const Camera2D *camera)
{
    BeginMode2D(*camera);
}

void stark_raylib_BeginMode3D(const Camera3D *camera)
{
    BeginMode3D(*camera);
}

void stark_raylib_BeginTextureMode(const RenderTexture2D *target)
{
    BeginTextureMode(*target);
}

void stark_raylib_BeginShaderMode(const Shader *shader)
{
    BeginShaderMode(*shader);
}

void stark_raylib_BeginVrStereoMode(const VrStereoConfig *config)
{
    BeginVrStereoMode(*config);
}

void stark_raylib_LoadVrStereoConfig(VrStereoConfig *result, const VrDeviceInfo *device)
{
    *result = LoadVrStereoConfig(*device);
}

void stark_raylib_UnloadVrStereoConfig(const VrStereoConfig *config)
{
    UnloadVrStereoConfig(*config);
}

void stark_raylib_LoadShader(Shader *result, const char * vsFileName, const char * fsFileName)
{
    *result = LoadShader(vsFileName, fsFileName);
}

void stark_raylib_LoadShaderFromMemory(Shader *result, const char * vsCode, const char * fsCode)
{
    *result = LoadShaderFromMemory(vsCode, fsCode);
}

bool stark_raylib_IsShaderValid(const Shader *shader)
{
    return IsShaderValid(*shader);
}

int stark_raylib_GetShaderLocation(const Shader *shader, const char * uniformName)
{
    return GetShaderLocation(*shader, uniformName);
}

int stark_raylib_GetShaderLocationAttrib(const Shader *shader, const char * attribName)
{
    return GetShaderLocationAttrib(*shader, attribName);
}

void stark_raylib_SetShaderValue(const Shader *shader, int locIndex, const void * value, int uniformType)
{
    SetShaderValue(*shader, locIndex, value, uniformType);
}

void stark_raylib_SetShaderValueV(const Shader *shader, int locIndex, const void * value, int uniformType, int count)
{
    SetShaderValueV(*shader, locIndex, value, uniformType, count);
}

void stark_raylib_SetShaderValueMatrix(const Shader *shader, int locIndex, const Matrix *mat)
{
    SetShaderValueMatrix(*shader, locIndex, *mat);
}

void stark_raylib_SetShaderValueTexture(const Shader *shader, int locIndex, const Texture2D *texture)
{
    SetShaderValueTexture(*shader, locIndex, *texture);
}

void stark_raylib_UnloadShader(const Shader *shader)
{
    UnloadShader(*shader);
}

void stark_raylib_GetScreenToWorldRay(Ray *result, const Vector2 *position, const Camera *camera)
{
    *result = GetScreenToWorldRay(*position, *camera);
}

void stark_raylib_GetScreenToWorldRayEx(Ray *result, const Vector2 *position, const Camera *camera, int width, int height)
{
    *result = GetScreenToWorldRayEx(*position, *camera, width, height);
}

void stark_raylib_GetWorldToScreen(Vector2 *result, const Vector3 *position, const Camera *camera)
{
    *result = GetWorldToScreen(*position, *camera);
}

void stark_raylib_GetWorldToScreenEx(Vector2 *result, const Vector3 *position, const Camera *camera, int width, int height)
{
    *result = GetWorldToScreenEx(*position, *camera, width, height);
}

void stark_raylib_GetWorldToScreen2D(Vector2 *result, const Vector2 *position, const Camera2D *camera)
{
    *result = GetWorldToScreen2D(*position, *camera);
}

void stark_raylib_GetScreenToWorld2D(Vector2 *result, const Vector2 *position, const Camera2D *camera)
{
    *result = GetScreenToWorld2D(*position, *camera);
}

void stark_raylib_GetCameraMatrix(Matrix *result, const Camera *camera)
{
    *result = GetCameraMatrix(*camera);
}

void stark_raylib_GetCameraMatrix2D(Matrix *result, const Camera2D *camera)
{
    *result = GetCameraMatrix2D(*camera);
}

void stark_raylib_LoadDirectoryFiles(FilePathList *result, const char * dirPath)
{
    *result = LoadDirectoryFiles(dirPath);
}

void stark_raylib_LoadDirectoryFilesEx(FilePathList *result, const char * basePath, const char * filter, bool scanSubdirs)
{
    *result = LoadDirectoryFilesEx(basePath, filter, scanSubdirs);
}

void stark_raylib_UnloadDirectoryFiles(const FilePathList *files)
{
    UnloadDirectoryFiles(*files);
}

void stark_raylib_LoadDroppedFiles(FilePathList *result)
{
    *result = LoadDroppedFiles();
}

void stark_raylib_UnloadDroppedFiles(const FilePathList *files)
{
    UnloadDroppedFiles(*files);
}

void stark_raylib_LoadAutomationEventList(AutomationEventList *result, const char * fileName)
{
    *result = LoadAutomationEventList(fileName);
}

void stark_raylib_UnloadAutomationEventList(const AutomationEventList *list)
{
    UnloadAutomationEventList(*list);
}

bool stark_raylib_ExportAutomationEventList(const AutomationEventList *list, const char * fileName)
{
    return ExportAutomationEventList(*list, fileName);
}

void stark_raylib_PlayAutomationEvent(const AutomationEvent *event)
{
    PlayAutomationEvent(*event);
}

long long stark_raylib_GetFileModTime(const char * fileName)
{
    return (long long)GetFileModTime(fileName);
}

void stark_raylib_GetMousePosition(Vector2 *result)
{
    *result = GetMousePosition();
}

void stark_raylib_GetMouseDelta(Vector2 *result)
{
    *result = GetMouseDelta();
}

void stark_raylib_GetMouseWheelMoveV(Vector2 *result)
{
    *result = GetMouseWheelMoveV();
}

void stark_raylib_GetTouchPosition(Vector2 *result, int index)
{
    *result = GetTouchPosition(index);
}

void stark_raylib_GetGestureDragVector(Vector2 *result)
{
    *result = GetGestureDragVector();
}

void stark_raylib_GetGesturePinchVector(Vector2 *result)
{
    *result = GetGesturePinchVector();
}

void stark_raylib_UpdateCameraPro(Camera * camera, const Vector3 *movement, const Vector3 *rotation, float zoom)
{
    UpdateCameraPro(camera, *movement, *rotation, zoom);
}

void stark_raylib_SetShapesTexture(const Texture2D *texture, const Rectangle *source)
{
    SetShapesTexture(*texture, *source);
}

void stark_raylib_GetShapesTexture(Texture2D *result)
{
    *result = GetShapesTexture();
}

void stark_raylib_GetShapesTextureRectangle(Rectangle *result)
{
    *result = GetShapesTextureRectangle();
}

void stark_raylib_DrawPixel(int posX, int posY, const Color *color)
{
    DrawPixel(posX, posY, *color);
}

void stark_raylib_DrawPixelV(const Vector2 *position, const Color *color)
{
    DrawPixelV(*position, *color);
}

void stark_raylib_DrawLine(int startPosX, int startPosY, int endPosX, int endPosY, const Color *color)
{
    DrawLine(startPosX, startPosY, endPosX, endPosY, *color);
}

void stark_raylib_DrawLineV(const Vector2 *startPos, const Vector2 *endPos, const Color *color)
{
    DrawLineV(*startPos, *endPos, *color);
}

void stark_raylib_DrawLineEx(const Vector2 *startPos, const Vector2 *endPos, float thick, const Color *color)
{
    DrawLineEx(*startPos, *endPos, thick, *color);
}

void stark_raylib_DrawLineStrip(const Vector2 * points, int pointCount, const Color *color)
{
    DrawLineStrip(points, pointCount, *color);
}

void stark_raylib_DrawLineBezier(const Vector2 *startPos, const Vector2 *endPos, float thick, const Color *color)
{
    DrawLineBezier(*startPos, *endPos, thick, *color);
}

void stark_raylib_DrawCircle(int centerX, int centerY, float radius, const Color *color)
{
    DrawCircle(centerX, centerY, radius, *color);
}

void stark_raylib_DrawCircleSector(const Vector2 *center, float radius, float startAngle, float endAngle, int segments, const Color *color)
{
    DrawCircleSector(*center, radius, startAngle, endAngle, segments, *color);
}

void stark_raylib_DrawCircleSectorLines(const Vector2 *center, float radius, float startAngle, float endAngle, int segments, const Color *color)
{
    DrawCircleSectorLines(*center, radius, startAngle, endAngle, segments, *color);
}

void stark_raylib_DrawCircleGradient(int centerX, int centerY, float radius, const Color *inner, const Color *outer)
{
    DrawCircleGradient(centerX, centerY, radius, *inner, *outer);
}

void stark_raylib_DrawCircleV(const Vector2 *center, float radius, const Color *color)
{
    DrawCircleV(*center, radius, *color);
}

void stark_raylib_DrawCircleLines(int centerX, int centerY, float radius, const Color *color)
{
    DrawCircleLines(centerX, centerY, radius, *color);
}

void stark_raylib_DrawCircleLinesV(const Vector2 *center, float radius, const Color *color)
{
    DrawCircleLinesV(*center, radius, *color);
}

void stark_raylib_DrawEllipse(int centerX, int centerY, float radiusH, float radiusV, const Color *color)
{
    DrawEllipse(centerX, centerY, radiusH, radiusV, *color);
}

void stark_raylib_DrawEllipseLines(int centerX, int centerY, float radiusH, float radiusV, const Color *color)
{
    DrawEllipseLines(centerX, centerY, radiusH, radiusV, *color);
}

void stark_raylib_DrawRing(const Vector2 *center, float innerRadius, float outerRadius, float startAngle, float endAngle, int segments, const Color *color)
{
    DrawRing(*center, innerRadius, outerRadius, startAngle, endAngle, segments, *color);
}

void stark_raylib_DrawRingLines(const Vector2 *center, float innerRadius, float outerRadius, float startAngle, float endAngle, int segments, const Color *color)
{
    DrawRingLines(*center, innerRadius, outerRadius, startAngle, endAngle, segments, *color);
}

void stark_raylib_DrawRectangle(int posX, int posY, int width, int height, const Color *color)
{
    DrawRectangle(posX, posY, width, height, *color);
}

void stark_raylib_DrawRectangleV(const Vector2 *position, const Vector2 *size, const Color *color)
{
    DrawRectangleV(*position, *size, *color);
}

void stark_raylib_DrawRectangleRec(const Rectangle *rec, const Color *color)
{
    DrawRectangleRec(*rec, *color);
}

void stark_raylib_DrawRectanglePro(const Rectangle *rec, const Vector2 *origin, float rotation, const Color *color)
{
    DrawRectanglePro(*rec, *origin, rotation, *color);
}

void stark_raylib_DrawRectangleGradientV(int posX, int posY, int width, int height, const Color *top, const Color *bottom)
{
    DrawRectangleGradientV(posX, posY, width, height, *top, *bottom);
}

void stark_raylib_DrawRectangleGradientH(int posX, int posY, int width, int height, const Color *left, const Color *right)
{
    DrawRectangleGradientH(posX, posY, width, height, *left, *right);
}

void stark_raylib_DrawRectangleGradientEx(const Rectangle *rec, const Color *topLeft, const Color *bottomLeft, const Color *topRight, const Color *bottomRight)
{
    DrawRectangleGradientEx(*rec, *topLeft, *bottomLeft, *topRight, *bottomRight);
}

void stark_raylib_DrawRectangleLines(int posX, int posY, int width, int height, const Color *color)
{
    DrawRectangleLines(posX, posY, width, height, *color);
}

void stark_raylib_DrawRectangleLinesEx(const Rectangle *rec, float lineThick, const Color *color)
{
    DrawRectangleLinesEx(*rec, lineThick, *color);
}

void stark_raylib_DrawRectangleRounded(const Rectangle *rec, float roundness, int segments, const Color *color)
{
    DrawRectangleRounded(*rec, roundness, segments, *color);
}

void stark_raylib_DrawRectangleRoundedLines(const Rectangle *rec, float roundness, int segments, const Color *color)
{
    DrawRectangleRoundedLines(*rec, roundness, segments, *color);
}

void stark_raylib_DrawRectangleRoundedLinesEx(const Rectangle *rec, float roundness, int segments, float lineThick, const Color *color)
{
    DrawRectangleRoundedLinesEx(*rec, roundness, segments, lineThick, *color);
}

void stark_raylib_DrawTriangle(const Vector2 *v1, const Vector2 *v2, const Vector2 *v3, const Color *color)
{
    DrawTriangle(*v1, *v2, *v3, *color);
}

void stark_raylib_DrawTriangleLines(const Vector2 *v1, const Vector2 *v2, const Vector2 *v3, const Color *color)
{
    DrawTriangleLines(*v1, *v2, *v3, *color);
}

void stark_raylib_DrawTriangleFan(const Vector2 * points, int pointCount, const Color *color)
{
    DrawTriangleFan(points, pointCount, *color);
}

void stark_raylib_DrawTriangleStrip(const Vector2 * points, int pointCount, const Color *color)
{
    DrawTriangleStrip(points, pointCount, *color);
}

void stark_raylib_DrawPoly(const Vector2 *center, int sides, float radius, float rotation, const Color *color)
{
    DrawPoly(*center, sides, radius, rotation, *color);
}

void stark_raylib_DrawPolyLines(const Vector2 *center, int sides, float radius, float rotation, const Color *color)
{
    DrawPolyLines(*center, sides, radius, rotation, *color);
}

void stark_raylib_DrawPolyLinesEx(const Vector2 *center, int sides, float radius, float rotation, float lineThick, const Color *color)
{
    DrawPolyLinesEx(*center, sides, radius, rotation, lineThick, *color);
}

void stark_raylib_DrawSplineLinear(const Vector2 * points, int pointCount, float thick, const Color *color)
{
    DrawSplineLinear(points, pointCount, thick, *color);
}

void stark_raylib_DrawSplineBasis(const Vector2 * points, int pointCount, float thick, const Color *color)
{
    DrawSplineBasis(points, pointCount, thick, *color);
}

void stark_raylib_DrawSplineCatmullRom(const Vector2 * points, int pointCount, float thick, const Color *color)
{
    DrawSplineCatmullRom(points, pointCount, thick, *color);
}

void stark_raylib_DrawSplineBezierQuadratic(const Vector2 * points, int pointCount, float thick, const Color *color)
{
    DrawSplineBezierQuadratic(points, pointCount, thick, *color);
}

void stark_raylib_DrawSplineBezierCubic(const Vector2 * points, int pointCount, float thick, const Color *color)
{
    DrawSplineBezierCubic(points, pointCount, thick, *color);
}

void stark_raylib_DrawSplineSegmentLinear(const Vector2 *p1, const Vector2 *p2, float thick, const Color *color)
{
    DrawSplineSegmentLinear(*p1, *p2, thick, *color);
}

void stark_raylib_DrawSplineSegmentBasis(const Vector2 *p1, const Vector2 *p2, const Vector2 *p3, const Vector2 *p4, float thick, const Color *color)
{
    DrawSplineSegmentBasis(*p1, *p2, *p3, *p4, thick, *color);
}

void stark_raylib_DrawSplineSegmentCatmullRom(const Vector2 *p1, const Vector2 *p2, const Vector2 *p3, const Vector2 *p4, float thick, const Color *color)
{
    DrawSplineSegmentCatmullRom(*p1, *p2, *p3, *p4, thick, *color);
}

void stark_raylib_DrawSplineSegmentBezierQuadratic(const Vector2 *p1, const Vector2 *c2, const Vector2 *p3, float thick, const Color *color)
{
    DrawSplineSegmentBezierQuadratic(*p1, *c2, *p3, thick, *color);
}

void stark_raylib_DrawSplineSegmentBezierCubic(const Vector2 *p1, const Vector2 *c2, const Vector2 *c3, const Vector2 *p4, float thick, const Color *color)
{
    DrawSplineSegmentBezierCubic(*p1, *c2, *c3, *p4, thick, *color);
}

void stark_raylib_GetSplinePointLinear(Vector2 *result, const Vector2 *startPos, const Vector2 *endPos, float t)
{
    *result = GetSplinePointLinear(*startPos, *endPos, t);
}

void stark_raylib_GetSplinePointBasis(Vector2 *result, const Vector2 *p1, const Vector2 *p2, const Vector2 *p3, const Vector2 *p4, float t)
{
    *result = GetSplinePointBasis(*p1, *p2, *p3, *p4, t);
}

void stark_raylib_GetSplinePointCatmullRom(Vector2 *result, const Vector2 *p1, const Vector2 *p2, const Vector2 *p3, const Vector2 *p4, float t)
{
    *result = GetSplinePointCatmullRom(*p1, *p2, *p3, *p4, t);
}

void stark_raylib_GetSplinePointBezierQuad(Vector2 *result, const Vector2 *p1, const Vector2 *c2, const Vector2 *p3, float t)
{
    *result = GetSplinePointBezierQuad(*p1, *c2, *p3, t);
}

void stark_raylib_GetSplinePointBezierCubic(Vector2 *result, const Vector2 *p1, const Vector2 *c2, const Vector2 *c3, const Vector2 *p4, float t)
{
    *result = GetSplinePointBezierCubic(*p1, *c2, *c3, *p4, t);
}

bool stark_raylib_CheckCollisionRecs(const Rectangle *rec1, const Rectangle *rec2)
{
    return CheckCollisionRecs(*rec1, *rec2);
}

bool stark_raylib_CheckCollisionCircles(const Vector2 *center1, float radius1, const Vector2 *center2, float radius2)
{
    return CheckCollisionCircles(*center1, radius1, *center2, radius2);
}

bool stark_raylib_CheckCollisionCircleRec(const Vector2 *center, float radius, const Rectangle *rec)
{
    return CheckCollisionCircleRec(*center, radius, *rec);
}

bool stark_raylib_CheckCollisionCircleLine(const Vector2 *center, float radius, const Vector2 *p1, const Vector2 *p2)
{
    return CheckCollisionCircleLine(*center, radius, *p1, *p2);
}

bool stark_raylib_CheckCollisionPointRec(const Vector2 *point, const Rectangle *rec)
{
    return CheckCollisionPointRec(*point, *rec);
}

bool stark_raylib_CheckCollisionPointCircle(const Vector2 *point, const Vector2 *center, float radius)
{
    return CheckCollisionPointCircle(*point, *center, radius);
}

bool stark_raylib_CheckCollisionPointTriangle(const Vector2 *point, const Vector2 *p1, const Vector2 *p2, const Vector2 *p3)
{
    return CheckCollisionPointTriangle(*point, *p1, *p2, *p3);
}

bool stark_raylib_CheckCollisionPointLine(const Vector2 *point, const Vector2 *p1, const Vector2 *p2, int threshold)
{
    return CheckCollisionPointLine(*point, *p1, *p2, threshold);
}

bool stark_raylib_CheckCollisionPointPoly(const Vector2 *point, const Vector2 * points, int pointCount)
{
    return CheckCollisionPointPoly(*point, points, pointCount);
}

bool stark_raylib_CheckCollisionLines(const Vector2 *startPos1, const Vector2 *endPos1, const Vector2 *startPos2, const Vector2 *endPos2, Vector2 * collisionPoint)
{
    return CheckCollisionLines(*startPos1, *endPos1, *startPos2, *endPos2, collisionPoint);
}

void stark_raylib_GetCollisionRec(Rectangle *result, const Rectangle *rec1, const Rectangle *rec2)
{
    *result = GetCollisionRec(*rec1, *rec2);
}

void stark_raylib_LoadImage(Image *result, const char * fileName)
{
    *result = LoadImage(fileName);
}

void stark_raylib_LoadImageRaw(Image *result, const char * fileName, int width, int height, int format, int headerSize)
{
    *result = LoadImageRaw(fileName, width, height, format, headerSize);
}

void stark_raylib_LoadImageAnim(Image *result, const char * fileName, int * frames)
{
    *result = LoadImageAnim(fileName, frames);
}

void stark_raylib_LoadImageAnimFromMemory(Image *result, const char * fileType, const unsigned char * fileData, int dataSize, int * frames)
{
    *result = LoadImageAnimFromMemory(fileType, fileData, dataSize, frames);
}

void stark_raylib_LoadImageFromMemory(Image *result, const char * fileType, const unsigned char * fileData, int dataSize)
{
    *result = LoadImageFromMemory(fileType, fileData, dataSize);
}

void stark_raylib_LoadImageFromTexture(Image *result, const Texture2D *texture)
{
    *result = LoadImageFromTexture(*texture);
}

void stark_raylib_LoadImageFromScreen(Image *result)
{
    *result = LoadImageFromScreen();
}

bool stark_raylib_IsImageValid(const Image *image)
{
    return IsImageValid(*image);
}

void stark_raylib_UnloadImage(const Image *image)
{
    UnloadImage(*image);
}

bool stark_raylib_ExportImage(const Image *image, const char * fileName)
{
    return ExportImage(*image, fileName);
}

unsigned char * stark_raylib_ExportImageToMemory(const Image *image, const char * fileType, int * fileSize)
{
    return ExportImageToMemory(*image, fileType, fileSize);
}

bool stark_raylib_ExportImageAsCode(const Image *image, const char * fileName)
{
    return ExportImageAsCode(*image, fileName);
}

void stark_raylib_GenImageColor(Image *result, int width, int height, const Color *color)
{
    *result = GenImageColor(width, height, *color);
}

void stark_raylib_GenImageGradientLinear(Image *result, int width, int height, int direction, const Color *start, const Color *end)
{
    *result = GenImageGradientLinear(width, height, direction, *start, *end);
}

void stark_raylib_GenImageGradientRadial(Image *result, int width, int height, float density, const Color *inner, const Color *outer)
{
    *result = GenImageGradientRadial(width, height, density, *inner, *outer);
}

void stark_raylib_GenImageGradientSquare(Image *result, int width, int height, float density, const Color *inner, const Color *outer)
{
    *result = GenImageGradientSquare(width, height, density, *inner, *outer);
}

void stark_raylib_GenImageChecked(Image *result, int width, int height, int checksX, int checksY, const Color *col1, const Color *col2)
{
    *result = GenImageChecked(width, height, checksX, checksY, *col1, *col2);
}

void stark_raylib_GenImageWhiteNoise(Image *result, int width, int height, float factor)
{
    *result = GenImageWhiteNoise(width, height, factor);
}

void stark_raylib_GenImagePerlinNoise(Image *result, int width, int height, int offsetX, int offsetY, float scale)
{
    *result = GenImagePerlinNoise(width, height, offsetX, offsetY, scale);
}

void stark_raylib_GenImageCellular(Image *result, int width, int height, int tileSize)
{
    *result = GenImageCellular(width, height, tileSize);
}

void stark_raylib_GenImageText(Image *result, int width, int height, const char * text)
{
    *result = GenImageText(width, height, text);
}

void stark_raylib_ImageCopy(Image *result, const Image *image)
{
    *result = ImageCopy(*image);
}

void stark_raylib_ImageFromImage(Image *result, const Image *image, const Rectangle *rec)
{
    *result = ImageFromImage(*image, *rec);
}

void stark_raylib_ImageFromChannel(Image *result, const Image *image, int selectedChannel)
{
    *result = ImageFromChannel(*image, selectedChannel);
}

void stark_raylib_ImageText(Image *result, const char * text, int fontSize, const Color *color)
{
    *result = ImageText(text, fontSize, *color);
}

void stark_raylib_ImageTextEx(Image *result, const Font *font, const char * text, float fontSize, float spacing, const Color *tint)
{
    *result = ImageTextEx(*font, text, fontSize, spacing, *tint);
}

void stark_raylib_ImageToPOT(Image * image, const Color *fill)
{
    ImageToPOT(image, *fill);
}

void stark_raylib_ImageCrop(Image * image, const Rectangle *crop)
{
    ImageCrop(image, *crop);
}

void stark_raylib_ImageAlphaClear(Image * image, const Color *color, float threshold)
{
    ImageAlphaClear(image, *color, threshold);
}

void stark_raylib_ImageAlphaMask(Image * image, const Image *alphaMask)
{
    ImageAlphaMask(image, *alphaMask);
}

void stark_raylib_ImageResizeCanvas(Image * image, int newWidth, int newHeight, int offsetX, int offsetY, const Color *fill)
{
    ImageResizeCanvas(image, newWidth, newHeight, offsetX, offsetY, *fill);
}

void stark_raylib_ImageColorTint(Image * image, const Color *color)
{
    ImageColorTint(image, *color);
}

void stark_raylib_ImageColorReplace(Image * image, const Color *color, const Color *replace)
{
    ImageColorReplace(image, *color, *replace);
}

Color * stark_raylib_LoadImageColors(const Image *image)
{
    return LoadImageColors(*image);
}

Color * stark_raylib_LoadImagePalette(const Image *image, int maxPaletteSize, int * colorCount)
{
    return LoadImagePalette(*image, maxPaletteSize, colorCount);
}

void stark_raylib_GetImageAlphaBorder(Rectangle *result, const Image *image, float threshold)
{
    *result = GetImageAlphaBorder(*image, threshold);
}

void stark_raylib_GetImageColor(Color *result, const Image *image, int x, int y)
{
    *result = GetImageColor(*image, x, y);
}

void stark_raylib_ImageClearBackground(Image * dst, const Color *color)
{
    ImageClearBackground(dst, *color);
}

void stark_raylib_ImageDrawPixel(Image * dst, int posX, int posY, const Color *color)
{
    ImageDrawPixel(dst, posX, posY, *color);
}

void stark_raylib_ImageDrawPixelV(Image * dst, const Vector2 *position, const Color *color)
{
    ImageDrawPixelV(dst, *position, *color);
}

void stark_raylib_ImageDrawLine(Image * dst, int startPosX, int startPosY, int endPosX, int endPosY, const Color *color)
{
    ImageDrawLine(dst, startPosX, startPosY, endPosX, endPosY, *color);
}

void stark_raylib_ImageDrawLineV(Image * dst, const Vector2 *start, const Vector2 *end, const Color *color)
{
    ImageDrawLineV(dst, *start, *end, *color);
}

void stark_raylib_ImageDrawLineEx(Image * dst, const Vector2 *start, const Vector2 *end, int thick, const Color *color)
{
    ImageDrawLineEx(dst, *start, *end, thick, *color);
}

void stark_raylib_ImageDrawCircle(Image * dst, int centerX, int centerY, int radius, const Color *color)
{
    ImageDrawCircle(dst, centerX, centerY, radius, *color);
}

void stark_raylib_ImageDrawCircleV(Image * dst, const Vector2 *center, int radius, const Color *color)
{
    ImageDrawCircleV(dst, *center, radius, *color);
}

void stark_raylib_ImageDrawCircleLines(Image * dst, int centerX, int centerY, int radius, const Color *color)
{
    ImageDrawCircleLines(dst, centerX, centerY, radius, *color);
}

void stark_raylib_ImageDrawCircleLinesV(Image * dst, const Vector2 *center, int radius, const Color *color)
{
    ImageDrawCircleLinesV(dst, *center, radius, *color);
}

void stark_raylib_ImageDrawRectangle(Image * dst, int posX, int posY, int width, int height, const Color *color)
{
    ImageDrawRectangle(dst, posX, posY, width, height, *color);
}

void stark_raylib_ImageDrawRectangleV(Image * dst, const Vector2 *position, const Vector2 *size, const Color *color)
{
    ImageDrawRectangleV(dst, *position, *size, *color);
}

void stark_raylib_ImageDrawRectangleRec(Image * dst, const Rectangle *rec, const Color *color)
{
    ImageDrawRectangleRec(dst, *rec, *color);
}

void stark_raylib_ImageDrawRectangleLines(Image * dst, const Rectangle *rec, int thick, const Color *color)
{
    ImageDrawRectangleLines(dst, *rec, thick, *color);
}

void stark_raylib_ImageDrawTriangle(Image * dst, const Vector2 *v1, const Vector2 *v2, const Vector2 *v3, const Color *color)
{
    ImageDrawTriangle(dst, *v1, *v2, *v3, *color);
}

void stark_raylib_ImageDrawTriangleEx(Image * dst, const Vector2 *v1, const Vector2 *v2, const Vector2 *v3, const Color *c1, const Color *c2, const Color *c3)
{
    ImageDrawTriangleEx(dst, *v1, *v2, *v3, *c1, *c2, *c3);
}

void stark_raylib_ImageDrawTriangleLines(Image * dst, const Vector2 *v1, const Vector2 *v2, const Vector2 *v3, const Color *color)
{
    ImageDrawTriangleLines(dst, *v1, *v2, *v3, *color);
}

void stark_raylib_ImageDrawTriangleFan(Image * dst, Vector2 * points, int pointCount, const Color *color)
{
    ImageDrawTriangleFan(dst, points, pointCount, *color);
}

void stark_raylib_ImageDrawTriangleStrip(Image * dst, Vector2 * points, int pointCount, const Color *color)
{
    ImageDrawTriangleStrip(dst, points, pointCount, *color);
}

void stark_raylib_ImageDraw(Image * dst, const Image *src, const Rectangle *srcRec, const Rectangle *dstRec, const Color *tint)
{
    ImageDraw(dst, *src, *srcRec, *dstRec, *tint);
}

void stark_raylib_ImageDrawText(Image * dst, const char * text, int posX, int posY, int fontSize, const Color *color)
{
    ImageDrawText(dst, text, posX, posY, fontSize, *color);
}

void stark_raylib_ImageDrawTextEx(Image * dst, const Font *font, const char * text, const Vector2 *position, float fontSize, float spacing, const Color *tint)
{
    ImageDrawTextEx(dst, *font, text, *position, fontSize, spacing, *tint);
}

void stark_raylib_LoadTexture(Texture2D *result, const char * fileName)
{
    *result = LoadTexture(fileName);
}

void stark_raylib_LoadTextureFromImage(Texture2D *result, const Image *image)
{
    *result = LoadTextureFromImage(*image);
}

void stark_raylib_LoadTextureCubemap(TextureCubemap *result, const Image *image, int layout)
{
    *result = LoadTextureCubemap(*image, layout);
}

void stark_raylib_LoadRenderTexture(RenderTexture2D *result, int width, int height)
{
    *result = LoadRenderTexture(width, height);
}

bool stark_raylib_IsTextureValid(const Texture2D *texture)
{
    return IsTextureValid(*texture);
}

void stark_raylib_UnloadTexture(const Texture2D *texture)
{
    UnloadTexture(*texture);
}

bool stark_raylib_IsRenderTextureValid(const RenderTexture2D *target)
{
    return IsRenderTextureValid(*target);
}

void stark_raylib_UnloadRenderTexture(const RenderTexture2D *target)
{
    UnloadRenderTexture(*target);
}

void stark_raylib_UpdateTexture(const Texture2D *texture, const void * pixels)
{
    UpdateTexture(*texture, pixels);
}

void stark_raylib_UpdateTextureRec(const Texture2D *texture, const Rectangle *rec, const void * pixels)
{
    UpdateTextureRec(*texture, *rec, pixels);
}

void stark_raylib_SetTextureFilter(const Texture2D *texture, int filter)
{
    SetTextureFilter(*texture, filter);
}

void stark_raylib_SetTextureWrap(const Texture2D *texture, int wrap)
{
    SetTextureWrap(*texture, wrap);
}

void stark_raylib_DrawTexture(const Texture2D *texture, int posX, int posY, const Color *tint)
{
    DrawTexture(*texture, posX, posY, *tint);
}

void stark_raylib_DrawTextureV(const Texture2D *texture, const Vector2 *position, const Color *tint)
{
    DrawTextureV(*texture, *position, *tint);
}

void stark_raylib_DrawTextureEx(const Texture2D *texture, const Vector2 *position, float rotation, float scale, const Color *tint)
{
    DrawTextureEx(*texture, *position, rotation, scale, *tint);
}

void stark_raylib_DrawTextureRec(const Texture2D *texture, const Rectangle *source, const Vector2 *position, const Color *tint)
{
    DrawTextureRec(*texture, *source, *position, *tint);
}

void stark_raylib_DrawTexturePro(const Texture2D *texture, const Rectangle *source, const Rectangle *dest, const Vector2 *origin, float rotation, const Color *tint)
{
    DrawTexturePro(*texture, *source, *dest, *origin, rotation, *tint);
}

void stark_raylib_DrawTextureNPatch(const Texture2D *texture, const NPatchInfo *nPatchInfo, const Rectangle *dest, const Vector2 *origin, float rotation, const Color *tint)
{
    DrawTextureNPatch(*texture, *nPatchInfo, *dest, *origin, rotation, *tint);
}

bool stark_raylib_ColorIsEqual(const Color *col1, const Color *col2)
{
    return ColorIsEqual(*col1, *col2);
}

void stark_raylib_Fade(Color *result, const Color *color, float alpha)
{
    *result = Fade(*color, alpha);
}

int stark_raylib_ColorToInt(const Color *color)
{
    return ColorToInt(*color);
}

void stark_raylib_ColorNormalize(Vector4 *result, const Color *color)
{
    *result = ColorNormalize(*color);
}

void stark_raylib_ColorFromNormalized(Color *result, const Vector4 *normalized)
{
    *result = ColorFromNormalized(*normalized);
}

void stark_raylib_ColorToHSV(Vector3 *result, const Color *color)
{
    *result = ColorToHSV(*color);
}

void stark_raylib_ColorFromHSV(Color *result, float hue, float saturation, float value)
{
    *result = ColorFromHSV(hue, saturation, value);
}

void stark_raylib_ColorTint(Color *result, const Color *color, const Color *tint)
{
    *result = ColorTint(*color, *tint);
}

void stark_raylib_ColorBrightness(Color *result, const Color *color, float factor)
{
    *result = ColorBrightness(*color, factor);
}

void stark_raylib_ColorContrast(Color *result, const Color *color, float contrast)
{
    *result = ColorContrast(*color, contrast);
}

void stark_raylib_ColorAlpha(Color *result, const Color *color, float alpha)
{
    *result = ColorAlpha(*color, alpha);
}

void stark_raylib_ColorAlphaBlend(Color *result, const Color *dst, const Color *src, const Color *tint)
{
    *result = ColorAlphaBlend(*dst, *src, *tint);
}

void stark_raylib_ColorLerp(Color *result, const Color *color1, const Color *color2, float factor)
{
    *result = ColorLerp(*color1, *color2, factor);
}

void stark_raylib_GetColor(Color *result, unsigned int hexValue)
{
    *result = GetColor(hexValue);
}

void stark_raylib_GetPixelColor(Color *result, void * srcPtr, int format)
{
    *result = GetPixelColor(srcPtr, format);
}

void stark_raylib_SetPixelColor(void * dstPtr, const Color *color, int format)
{
    SetPixelColor(dstPtr, *color, format);
}

void stark_raylib_GetFontDefault(Font *result)
{
    *result = GetFontDefault();
}

void stark_raylib_LoadFont(Font *result, const char * fileName)
{
    *result = LoadFont(fileName);
}

void stark_raylib_LoadFontEx(Font *result, const char * fileName, int fontSize, int * codepoints, int codepointCount)
{
    *result = LoadFontEx(fileName, fontSize, codepoints, codepointCount);
}

void stark_raylib_LoadFontFromImage(Font *result, const Image *image, const Color *key, int firstChar)
{
    *result = LoadFontFromImage(*image, *key, firstChar);
}

void stark_raylib_LoadFontFromMemory(Font *result, const char * fileType, const unsigned char * fileData, int dataSize, int fontSize, int * codepoints, int codepointCount)
{
    *result = LoadFontFromMemory(fileType, fileData, dataSize, fontSize, codepoints, codepointCount);
}

bool stark_raylib_IsFontValid(const Font *font)
{
    return IsFontValid(*font);
}

void stark_raylib_GenImageFontAtlas(Image *result, const GlyphInfo * glyphs, Rectangle ** glyphRecs, int glyphCount, int fontSize, int padding, int packMethod)
{
    *result = GenImageFontAtlas(glyphs, glyphRecs, glyphCount, fontSize, padding, packMethod);
}

void stark_raylib_UnloadFont(const Font *font)
{
    UnloadFont(*font);
}

bool stark_raylib_ExportFontAsCode(const Font *font, const char * fileName)
{
    return ExportFontAsCode(*font, fileName);
}

void stark_raylib_DrawText(const char * text, int posX, int posY, int fontSize, const Color *color)
{
    DrawText(text, posX, posY, fontSize, *color);
}

void stark_raylib_DrawTextEx(const Font *font, const char * text, const Vector2 *position, float fontSize, float spacing, const Color *tint)
{
    DrawTextEx(*font, text, *position, fontSize, spacing, *tint);
}

void stark_raylib_DrawTextPro(const Font *font, const char * text, const Vector2 *position, const Vector2 *origin, float rotation, float fontSize, float spacing, const Color *tint)
{
    DrawTextPro(*font, text, *position, *origin, rotation, fontSize, spacing, *tint);
}

void stark_raylib_DrawTextCodepoint(const Font *font, int codepoint, const Vector2 *position, float fontSize, const Color *tint)
{
    DrawTextCodepoint(*font, codepoint, *position, fontSize, *tint);
}

void stark_raylib_DrawTextCodepoints(const Font *font, const int * codepoints, int codepointCount, const Vector2 *position, float fontSize, float spacing, const Color *tint)
{
    DrawTextCodepoints(*font, codepoints, codepointCount, *position, fontSize, spacing, *tint);
}

void stark_raylib_MeasureTextEx(Vector2 *result, const Font *font, const char * text, float fontSize, float spacing)
{
    *result = MeasureTextEx(*font, text, fontSize, spacing);
}

int stark_raylib_GetGlyphIndex(const Font *font, int codepoint)
{
    return GetGlyphIndex(*font, codepoint);
}

void stark_raylib_GetGlyphInfo(GlyphInfo *result, const Font *font, int codepoint)
{
    *result = GetGlyphInfo(*font, codepoint);
}

void stark_raylib_GetGlyphAtlasRec(Rectangle *result, const Font *font, int codepoint)
{
    *result = GetGlyphAtlasRec(*font, codepoint);
}

void stark_raylib_DrawLine3D(const Vector3 *startPos, const Vector3 *endPos, const Color *color)
{
    DrawLine3D(*startPos, *endPos, *color);
}

void stark_raylib_DrawPoint3D(const Vector3 *position, const Color *color)
{
    DrawPoint3D(*position, *color);
}

void stark_raylib_DrawCircle3D(const Vector3 *center, float radius, const Vector3 *rotationAxis, float rotationAngle, const Color *color)
{
    DrawCircle3D(*center, radius, *rotationAxis, rotationAngle, *color);
}

void stark_raylib_DrawTriangle3D(const Vector3 *v1, const Vector3 *v2, const Vector3 *v3, const Color *color)
{
    DrawTriangle3D(*v1, *v2, *v3, *color);
}

void stark_raylib_DrawTriangleStrip3D(const Vector3 * points, int pointCount, const Color *color)
{
    DrawTriangleStrip3D(points, pointCount, *color);
}

void stark_raylib_DrawCube(const Vector3 *position, float width, float height, float length, const Color *color)
{
    DrawCube(*position, width, height, length, *color);
}

void stark_raylib_DrawCubeV(const Vector3 *position, const Vector3 *size, const Color *color)
{
    DrawCubeV(*position, *size, *color);
}

void stark_raylib_DrawCubeWires(const Vector3 *position, float width, float height, float length, const Color *color)
{
    DrawCubeWires(*position, width, height, length, *color);
}

void stark_raylib_DrawCubeWiresV(const Vector3 *position, const Vector3 *size, const Color *color)
{
    DrawCubeWiresV(*position, *size, *color);
}

void stark_raylib_DrawSphere(const Vector3 *centerPos, float radius, const Color *color)
{
    DrawSphere(*centerPos, radius, *color);
}

void stark_raylib_DrawSphereEx(const Vector3 *centerPos, float radius, int rings, int slices, const Color *color)
{
    DrawSphereEx(*centerPos, radius, rings, slices, *color);
}

void stark_raylib_DrawSphereWires(const Vector3 *centerPos, float radius, int rings, int slices, const Color *color)
{
    DrawSphereWires(*centerPos, radius, rings, slices, *color);
}

void stark_raylib_DrawCylinder(const Vector3 *position, float radiusTop, float radiusBottom, float height, int slices, const Color *color)
{
    DrawCylinder(*position, radiusTop, radiusBottom, height, slices, *color);
}

void stark_raylib_DrawCylinderEx(const Vector3 *startPos, const Vector3 *endPos, float startRadius, float endRadius, int sides, const Color *color)
{
    DrawCylinderEx(*startPos, *endPos, startRadius, endRadius, sides, *color);
}

void stark_raylib_DrawCylinderWires(const Vector3 *position, float radiusTop, float radiusBottom, float height, int slices, const Color *color)
{
    DrawCylinderWires(*position, radiusTop, radiusBottom, height, slices, *color);
}

void stark_raylib_DrawCylinderWiresEx(const Vector3 *startPos, const Vector3 *endPos, float startRadius, float endRadius, int sides, const Color *color)
{
    DrawCylinderWiresEx(*startPos, *endPos, startRadius, endRadius, sides, *color);
}

void stark_raylib_DrawCapsule(const Vector3 *startPos, const Vector3 *endPos, float radius, int slices, int rings, const Color *color)
{
    DrawCapsule(*startPos, *endPos, radius, slices, rings, *color);
}

void stark_raylib_DrawCapsuleWires(const Vector3 *startPos, const Vector3 *endPos, float radius, int slices, int rings, const Color *color)
{
    DrawCapsuleWires(*startPos, *endPos, radius, slices, rings, *color);
}

void stark_raylib_DrawPlane(const Vector3 *centerPos, const Vector2 *size, const Color *color)
{
    DrawPlane(*centerPos, *size, *color);
}

void stark_raylib_DrawRay(const Ray *ray, const Color *color)
{
    DrawRay(*ray, *color);
}

void stark_raylib_LoadModel(Model *result, const char * fileName)
{
    *result = LoadModel(fileName);
}

void stark_raylib_LoadModelFromMesh(Model *result, const Mesh *mesh)
{
    *result = LoadModelFromMesh(*mesh);
}

bool stark_raylib_IsModelValid(const Model *model)
{
    return IsModelValid(*model);
}

void stark_raylib_UnloadModel(const Model *model)
{
    UnloadModel(*model);
}

void stark_raylib_GetModelBoundingBox(BoundingBox *result, const Model *model)
{
    *result = GetModelBoundingBox(*model);
}

void stark_raylib_DrawModel(const Model *model, const Vector3 *position, float scale, const Color *tint)
{
    DrawModel(*model, *position, scale, *tint);
}

void stark_raylib_DrawModelEx(const Model *model, const Vector3 *position, const Vector3 *rotationAxis, float rotationAngle, const Vector3 *scale, const Color *tint)
{
    DrawModelEx(*model, *position, *rotationAxis, rotationAngle, *scale, *tint);
}

void stark_raylib_DrawModelWires(const Model *model, const Vector3 *position, float scale, const Color *tint)
{
    DrawModelWires(*model, *position, scale, *tint);
}

void stark_raylib_DrawModelWiresEx(const Model *model, const Vector3 *position, const Vector3 *rotationAxis, float rotationAngle, const Vector3 *scale, const Color *tint)
{
    DrawModelWiresEx(*model, *position, *rotationAxis, rotationAngle, *scale, *tint);
}

void stark_raylib_DrawModelPoints(const Model *model, const Vector3 *position, float scale, const Color *tint)
{
    DrawModelPoints(*model, *position, scale, *tint);
}

void stark_raylib_DrawModelPointsEx(const Model *model, const Vector3 *position, const Vector3 *rotationAxis, float rotationAngle, const Vector3 *scale, const Color *tint)
{
    DrawModelPointsEx(*model, *position, *rotationAxis, rotationAngle, *scale, *tint);
}

void stark_raylib_DrawBoundingBox(const BoundingBox *box, const Color *color)
{
    DrawBoundingBox(*box, *color);
}

void stark_raylib_DrawBillboard(const Camera *camera, const Texture2D *texture, const Vector3 *position, float scale, const Color *tint)
{
    DrawBillboard(*camera, *texture, *position, scale, *tint);
}

void stark_raylib_DrawBillboardRec(const Camera *camera, const Texture2D *texture, const Rectangle *source, const Vector3 *position, const Vector2 *size, const Color *tint)
{
    DrawBillboardRec(*camera, *texture, *source, *position, *size, *tint);
}

void stark_raylib_DrawBillboardPro(const Camera *camera, const Texture2D *texture, const Rectangle *source, const Vector3 *position, const Vector3 *up, const Vector2 *size, const Vector2 *origin, float rotation, const Color *tint)
{
    DrawBillboardPro(*camera, *texture, *source, *position, *up, *size, *origin, rotation, *tint);
}

void stark_raylib_UpdateMeshBuffer(const Mesh *mesh, int index, const void * data, int dataSize, int offset)
{
    UpdateMeshBuffer(*mesh, index, data, dataSize, offset);
}

void stark_raylib_UnloadMesh(const Mesh *mesh)
{
    UnloadMesh(*mesh);
}

void stark_raylib_DrawMesh(const Mesh *mesh, const Material *material, const Matrix *transform)
{
    DrawMesh(*mesh, *material, *transform);
}

void stark_raylib_DrawMeshInstanced(const Mesh *mesh, const Material *material, const Matrix * transforms, int instances)
{
    DrawMeshInstanced(*mesh, *material, transforms, instances);
}

void stark_raylib_GetMeshBoundingBox(BoundingBox *result, const Mesh *mesh)
{
    *result = GetMeshBoundingBox(*mesh);
}

bool stark_raylib_ExportMesh(const Mesh *mesh, const char * fileName)
{
    return ExportMesh(*mesh, fileName);
}

bool stark_raylib_ExportMeshAsCode(const Mesh *mesh, const char * fileName)
{
    return ExportMeshAsCode(*mesh, fileName);
}

void stark_raylib_GenMeshPoly(Mesh *result, int sides, float radius)
{
    *result = GenMeshPoly(sides, radius);
}

void stark_raylib_GenMeshPlane(Mesh *result, float width, float length, int resX, int resZ)
{
    *result = GenMeshPlane(width, length, resX, resZ);
}

void stark_raylib_GenMeshCube(Mesh *result, float width, float height, float length)
{
    *result = GenMeshCube(width, height, length);
}

void stark_raylib_GenMeshSphere(Mesh *result, float radius, int rings, int slices)
{
    *result = GenMeshSphere(radius, rings, slices);
}

void stark_raylib_GenMeshHemiSphere(Mesh *result, float radius, int rings, int slices)
{
    *result = GenMeshHemiSphere(radius, rings, slices);
}

void stark_raylib_GenMeshCylinder(Mesh *result, float radius, float height, int slices)
{
    *result = GenMeshCylinder(radius, height, slices);
}

void stark_raylib_GenMeshCone(Mesh *result, float radius, float height, int slices)
{
    *result = GenMeshCone(radius, height, slices);
}

void stark_raylib_GenMeshTorus(Mesh *result, float radius, float size, int radSeg, int sides)
{
    *result = GenMeshTorus(radius, size, radSeg, sides);
}

void stark_raylib_GenMeshKnot(Mesh *result, float radius, float size, int radSeg, int sides)
{
    *result = GenMeshKnot(radius, size, radSeg, sides);
}

void stark_raylib_GenMeshHeightmap(Mesh *result, const Image *heightmap, const Vector3 *size)
{
    *result = GenMeshHeightmap(*heightmap, *size);
}

void stark_raylib_GenMeshCubicmap(Mesh *result, const Image *cubicmap, const Vector3 *cubeSize)
{
    *result = GenMeshCubicmap(*cubicmap, *cubeSize);
}

void stark_raylib_LoadMaterialDefault(Material *result)
{
    *result = LoadMaterialDefault();
}

bool stark_raylib_IsMaterialValid(const Material *material)
{
    return IsMaterialValid(*material);
}

void stark_raylib_UnloadMaterial(const Material *material)
{
    UnloadMaterial(*material);
}

void stark_raylib_SetMaterialTexture(Material * material, int mapType, const Texture2D *texture)
{
    SetMaterialTexture(material, mapType, *texture);
}

void stark_raylib_UpdateModelAnimation(const Model *model, const ModelAnimation *anim, int frame)
{
    UpdateModelAnimation(*model, *anim, frame);
}

void stark_raylib_UpdateModelAnimationBones(const Model *model, const ModelAnimation *anim, int frame)
{
    UpdateModelAnimationBones(*model, *anim, frame);
}

void stark_raylib_UnloadModelAnimation(const ModelAnimation *anim)
{
    UnloadModelAnimation(*anim);
}

bool stark_raylib_IsModelAnimationValid(const Model *model, const ModelAnimation *anim)
{
    return IsModelAnimationValid(*model, *anim);
}

bool stark_raylib_CheckCollisionSpheres(const Vector3 *center1, float radius1, const Vector3 *center2, float radius2)
{
    return CheckCollisionSpheres(*center1, radius1, *center2, radius2);
}

bool stark_raylib_CheckCollisionBoxes(const BoundingBox *box1, const BoundingBox *box2)
{
    return CheckCollisionBoxes(*box1, *box2);
}

bool stark_raylib_CheckCollisionBoxSphere(const BoundingBox *box, const Vector3 *center, float radius)
{
    return CheckCollisionBoxSphere(*box, *center, radius);
}

void stark_raylib_GetRayCollisionSphere(RayCollision *result, const Ray *ray, const Vector3 *center, float radius)
{
    *result = GetRayCollisionSphere(*ray, *center, radius);
}

void stark_raylib_GetRayCollisionBox(RayCollision *result, const Ray *ray, const BoundingBox *box)
{
    *result = GetRayCollisionBox(*ray, *box);
}

void stark_raylib_GetRayCollisionMesh(RayCollision *result, const Ray *ray, const Mesh *mesh, const Matrix *transform)
{
    *result = GetRayCollisionMesh(*ray, *mesh, *transform);
}

void stark_raylib_GetRayCollisionTriangle(RayCollision *result, const Ray *ray, const Vector3 *p1, const Vector3 *p2, const Vector3 *p3)
{
    *result = GetRayCollisionTriangle(*ray, *p1, *p2, *p3);
}

void stark_raylib_GetRayCollisionQuad(RayCollision *result, const Ray *ray, const Vector3 *p1, const Vector3 *p2, const Vector3 *p3, const Vector3 *p4)
{
    *result = GetRayCollisionQuad(*ray, *p1, *p2, *p3, *p4);
}

void stark_raylib_LoadWave(Wave *result, const char * fileName)
{
    *result = LoadWave(fileName);
}

void stark_raylib_LoadWaveFromMemory(Wave *result, const char * fileType, const unsigned char * fileData, int dataSize)
{
    *result = LoadWaveFromMemory(fileType, fileData, dataSize);
}

bool stark_raylib_IsWaveValid(const Wave *wave)
{
    return IsWaveValid(*wave);
}

void stark_raylib_LoadSound(Sound *result, const char * fileName)
{
    *result = LoadSound(fileName);
}

void stark_raylib_LoadSoundFromWave(Sound *result, const Wave *wave)
{
    *result = LoadSoundFromWave(*wave);
}

void stark_raylib_LoadSoundAlias(Sound *result, const Sound *source)
{
    *result = LoadSoundAlias(*source);
}

bool stark_raylib_IsSoundValid(const Sound *sound)
{
    return IsSoundValid(*sound);
}

void stark_raylib_UpdateSound(const Sound *sound, const void * data, int sampleCount)
{
    UpdateSound(*sound, data, sampleCount);
}

void stark_raylib_UnloadWave(const Wave *wave)
{
    UnloadWave(*wave);
}

void stark_raylib_UnloadSound(const Sound *sound)
{
    UnloadSound(*sound);
}

void stark_raylib_UnloadSoundAlias(const Sound *aliasValue)
{
    UnloadSoundAlias(*aliasValue);
}

bool stark_raylib_ExportWave(const Wave *wave, const char * fileName)
{
    return ExportWave(*wave, fileName);
}

bool stark_raylib_ExportWaveAsCode(const Wave *wave, const char * fileName)
{
    return ExportWaveAsCode(*wave, fileName);
}

void stark_raylib_PlaySound(const Sound *sound)
{
    PlaySound(*sound);
}

void stark_raylib_StopSound(const Sound *sound)
{
    StopSound(*sound);
}

void stark_raylib_PauseSound(const Sound *sound)
{
    PauseSound(*sound);
}

void stark_raylib_ResumeSound(const Sound *sound)
{
    ResumeSound(*sound);
}

bool stark_raylib_IsSoundPlaying(const Sound *sound)
{
    return IsSoundPlaying(*sound);
}

void stark_raylib_SetSoundVolume(const Sound *sound, float volume)
{
    SetSoundVolume(*sound, volume);
}

void stark_raylib_SetSoundPitch(const Sound *sound, float pitch)
{
    SetSoundPitch(*sound, pitch);
}

void stark_raylib_SetSoundPan(const Sound *sound, float pan)
{
    SetSoundPan(*sound, pan);
}

void stark_raylib_WaveCopy(Wave *result, const Wave *wave)
{
    *result = WaveCopy(*wave);
}

float * stark_raylib_LoadWaveSamples(const Wave *wave)
{
    return LoadWaveSamples(*wave);
}

void stark_raylib_LoadMusicStream(Music *result, const char * fileName)
{
    *result = LoadMusicStream(fileName);
}

void stark_raylib_LoadMusicStreamFromMemory(Music *result, const char * fileType, const unsigned char * data, int dataSize)
{
    *result = LoadMusicStreamFromMemory(fileType, data, dataSize);
}

bool stark_raylib_IsMusicValid(const Music *music)
{
    return IsMusicValid(*music);
}

void stark_raylib_UnloadMusicStream(const Music *music)
{
    UnloadMusicStream(*music);
}

void stark_raylib_PlayMusicStream(const Music *music)
{
    PlayMusicStream(*music);
}

bool stark_raylib_IsMusicStreamPlaying(const Music *music)
{
    return IsMusicStreamPlaying(*music);
}

void stark_raylib_UpdateMusicStream(const Music *music)
{
    UpdateMusicStream(*music);
}

void stark_raylib_StopMusicStream(const Music *music)
{
    StopMusicStream(*music);
}

void stark_raylib_PauseMusicStream(const Music *music)
{
    PauseMusicStream(*music);
}

void stark_raylib_ResumeMusicStream(const Music *music)
{
    ResumeMusicStream(*music);
}

void stark_raylib_SeekMusicStream(const Music *music, float position)
{
    SeekMusicStream(*music, position);
}

void stark_raylib_SetMusicVolume(const Music *music, float volume)
{
    SetMusicVolume(*music, volume);
}

void stark_raylib_SetMusicPitch(const Music *music, float pitch)
{
    SetMusicPitch(*music, pitch);
}

void stark_raylib_SetMusicPan(const Music *music, float pan)
{
    SetMusicPan(*music, pan);
}

float stark_raylib_GetMusicTimeLength(const Music *music)
{
    return GetMusicTimeLength(*music);
}

float stark_raylib_GetMusicTimePlayed(const Music *music)
{
    return GetMusicTimePlayed(*music);
}

void stark_raylib_LoadAudioStream(AudioStream *result, unsigned int sampleRate, unsigned int sampleSize, unsigned int channels)
{
    *result = LoadAudioStream(sampleRate, sampleSize, channels);
}

bool stark_raylib_IsAudioStreamValid(const AudioStream *stream)
{
    return IsAudioStreamValid(*stream);
}

void stark_raylib_UnloadAudioStream(const AudioStream *stream)
{
    UnloadAudioStream(*stream);
}

void stark_raylib_UpdateAudioStream(const AudioStream *stream, const void * data, int frameCount)
{
    UpdateAudioStream(*stream, data, frameCount);
}

bool stark_raylib_IsAudioStreamProcessed(const AudioStream *stream)
{
    return IsAudioStreamProcessed(*stream);
}

void stark_raylib_PlayAudioStream(const AudioStream *stream)
{
    PlayAudioStream(*stream);
}

void stark_raylib_PauseAudioStream(const AudioStream *stream)
{
    PauseAudioStream(*stream);
}

void stark_raylib_ResumeAudioStream(const AudioStream *stream)
{
    ResumeAudioStream(*stream);
}

bool stark_raylib_IsAudioStreamPlaying(const AudioStream *stream)
{
    return IsAudioStreamPlaying(*stream);
}

void stark_raylib_StopAudioStream(const AudioStream *stream)
{
    StopAudioStream(*stream);
}

void stark_raylib_SetAudioStreamVolume(const AudioStream *stream, float volume)
{
    SetAudioStreamVolume(*stream, volume);
}

void stark_raylib_SetAudioStreamPitch(const AudioStream *stream, float pitch)
{
    SetAudioStreamPitch(*stream, pitch);
}

void stark_raylib_SetAudioStreamPan(const AudioStream *stream, float pan)
{
    SetAudioStreamPan(*stream, pan);
}

void stark_raylib_SetAudioStreamCallback(const AudioStream *stream, AudioCallback callback)
{
    SetAudioStreamCallback(*stream, callback);
}

void stark_raylib_AttachAudioStreamProcessor(const AudioStream *stream, AudioCallback processor)
{
    AttachAudioStreamProcessor(*stream, processor);
}

void stark_raylib_DetachAudioStreamProcessor(const AudioStream *stream, AudioCallback processor)
{
    DetachAudioStreamProcessor(*stream, processor);
}
