#import <AppKit/AppKit.h>
#import <QuartzCore/QuartzCore.h>
#import <objc/runtime.h>
#import <ImageIO/ImageIO.h>
#import <dispatch/dispatch.h>
#include <math.h>

static const void *WTOriginalUpdateKey = &WTOriginalUpdateKey;
static const void *WTSkinLayerKey = &WTSkinLayerKey;
static NSHashTable<NSView *> *wtViews;
static NSDictionary *wtTheme;
static NSDate *wtThemeDate;
static IMP wtOriginalMove;
static dispatch_source_t wtTimer;
static CGImageRef wtBackgroundImage;

static void wtIsolateInputServer(void) {
    // IMKServer is a class cluster on macOS 15. The allocated implementation
    // (currently _IMKServerLegacy) owns the initializer, not the facade.
    id serverPrototype = [NSClassFromString(@"IMKServer") alloc];
    Class cls = object_getClass(serverPrototype);
    SEL selector = NSSelectorFromString(@"initWithName:bundleIdentifier:");
    Method method = class_getInstanceMethod(cls, selector);
    fprintf(stderr, "[WeTypeSkin] server class=%s method=%s\n", cls ? class_getName(cls) : "missing", method ? "found" : "missing");
    if (!method) return;
    IMP original = method_getImplementation(method);
    IMP replacement = imp_implementationWithBlock(^id(id server, NSString *name, NSString *identifier) {
        NSString *cloneID = NSBundle.mainBundle.bundleIdentifier;
        NSString *connection = NSBundle.mainBundle.infoDictionary[@"InputMethodConnectionName"];
        id result = ((id (*)(id, SEL, id, id))original)(server, selector, connection, cloneID);
        NSLog(@"[WeTypeSkin] IMKServer connection=%@ bundle=%@ ready=%d", connection, cloneID, result != nil);
        return result;
    });
    method_setImplementation(method, replacement);
}

static BOOL wtIsSkinView(NSView *view) {
    NSString *name = NSStringFromClass(view.class);
    return [name containsString:@"CandidateContentView"] ||
           [name containsString:@"CandidatesSingleLineView"] ||
           [name containsString:@"CandidatesVerticalContentView"] ||
           [name containsString:@"StatusPill"] ||
           [name containsString:@"AIQuestToolBar"] ||
           [name containsString:@"Toolbar"];
}

static NSDictionary *wtReadTheme(void) {
    NSString *directory = [NSHomeDirectory() stringByAppendingPathComponent:@"Library/Application Support/WeTypeSkinStudio"];
    NSString *path = [directory stringByAppendingPathComponent:@"applied.wtskin.json"];
    if (![[NSFileManager defaultManager] fileExistsAtPath:path]) {
        path = [directory stringByAppendingPathComponent:@"current.wtskin.json"];
    }
    NSDictionary *attributes = [[NSFileManager defaultManager] attributesOfItemAtPath:path error:nil];
    NSDate *date = attributes[NSFileModificationDate];
    if (wtTheme && (!date || [date isEqualToDate:wtThemeDate])) return wtTheme;
    NSData *data = [NSData dataWithContentsOfFile:path options:NSDataReadingMappedIfSafe error:nil];
    if (data.length > 13 * 1024 * 1024) return wtTheme ?: @{};
    id object = data ? [NSJSONSerialization JSONObjectWithData:data options:0 error:nil] : nil;
    if (![object isKindOfClass:NSDictionary.class]) return wtTheme ?: @{};
    for (NSString *key in @[@"background", @"border"]) {
        id value = object[key];
        if (![value isKindOfClass:NSString.class] || [value length] != 7) return wtTheme ?: @{};
    }
    for (NSString *key in @[@"opacity", @"cornerRadius", @"imageTint", @"imageZoom", @"imagePositionX", @"imagePositionY"]) {
        id value = object[key];
        if (value && (![value isKindOfClass:NSNumber.class] || !isfinite([value doubleValue]))) return wtTheme ?: @{};
    }
    id base64 = object[@"backgroundImage"];
    if (base64 && ![base64 isKindOfClass:NSString.class]) return wtTheme ?: @{};
    if (wtBackgroundImage) { CGImageRelease(wtBackgroundImage); wtBackgroundImage = NULL; }
    if ([base64 length] > 0) {
        NSData *bytes = [[NSData alloc] initWithBase64EncodedString:base64 options:0];
        CGImageSourceRef source = bytes.length <= 8 * 1024 * 1024 ? CGImageSourceCreateWithData((__bridge CFDataRef)bytes, NULL) : NULL;
        if (source) {
            wtBackgroundImage = CGImageSourceCreateImageAtIndex(source, 0, NULL);
            CFRelease(source);
        }
    }
    wtThemeDate = date;
    wtTheme = object;
    return wtTheme;
}

static CGColorRef wtColor(NSString *value, CGFloat alpha) {
    if (![value isKindOfClass:NSString.class] || value.length != 7 || [value characterAtIndex:0] != '#') {
        return [[NSColor clearColor] colorUsingColorSpace:NSColorSpace.deviceRGBColorSpace].CGColor;
    }
    unsigned int rgb = 0;
    NSScanner *scanner = [NSScanner scannerWithString:[value substringFromIndex:1]];
    if (![scanner scanHexInt:&rgb]) return [NSColor clearColor].CGColor;
    return [NSColor colorWithSRGBRed:((rgb >> 16) & 0xff) / 255.0
                               green:((rgb >> 8) & 0xff) / 255.0
                                blue:(rgb & 0xff) / 255.0
                               alpha:alpha].CGColor;
}

static CALayer *wtImageLayer(CALayer *parent) {
    CALayer *image = objc_getAssociatedObject(parent, WTSkinLayerKey);
    if (!image) {
        image = [CALayer layer];
        image.name = @"WeTypeSkinStudio.background";
        image.zPosition = -100;
        image.contentsGravity = kCAGravityResize;
        [parent addSublayer:image];
        objc_setAssociatedObject(parent, WTSkinLayerKey, image, OBJC_ASSOCIATION_RETAIN_NONATOMIC);
    }
    return image;
}

static void wtApply(NSView *view) {
    if (!view.window || !wtIsSkinView(view)) return;
    NSDictionary *theme = wtReadTheme();
    if (theme.count == 0) return;
    NSNumber *opacity = theme[@"opacity"];
    NSNumber *radius = theme[@"cornerRadius"];
    BOOL candidate = [NSStringFromClass(view.class) containsString:@"Candidate"];
    BOOL toolbar = !candidate;
    NSString *background = theme[@"background"];
    NSString *border = theme[@"border"];
    CGFloat alpha = opacity.doubleValue > 0 ? opacity.doubleValue : 1;
    [CATransaction begin];
    [CATransaction setDisableActions:YES];
    view.wantsLayer = YES;
    CALayer *layer = view.layer;
    if (!layer) { [CATransaction commit]; return; }
    layer.backgroundColor = wtColor(background, candidate ? alpha : 1);
    layer.borderColor = wtColor(border, 1);
    layer.borderWidth = toolbar ? 1 : 0;
    layer.cornerRadius = MAX(0, MIN(40, radius.doubleValue));
    layer.masksToBounds = YES;
    CALayer *image = wtImageLayer(layer);
    image.hidden = wtBackgroundImage == NULL || !candidate;
    image.opacity = alpha;
    image.contents = (__bridge id)wtBackgroundImage;
    if (wtBackgroundImage && candidate) {
        CGFloat width = CGImageGetWidth(wtBackgroundImage), height = CGImageGetHeight(wtBackgroundImage);
        CGFloat zoom = MAX(1, MIN(4, [theme[@"imageZoom"] doubleValue] ?: 1));
        CGFloat scale = MAX(layer.bounds.size.width / width, layer.bounds.size.height / height) * zoom;
        CGFloat x = theme[@"imagePositionX"] ? MAX(0, MIN(1, [theme[@"imagePositionX"] doubleValue])) : 0.5;
        CGFloat y = theme[@"imagePositionY"] ? MAX(0, MIN(1, [theme[@"imagePositionY"] doubleValue])) : 0.5;
        image.frame = CGRectMake((layer.bounds.size.width - width * scale) * x,
                                 (layer.bounds.size.height - height * scale) * (view.isFlipped ? y : 1 - y),
                                 width * scale, height * scale);
    }
    [CATransaction commit];
}

static void wtHookClass(Class cls) {
    if (!cls) return;
    NSString *name = NSStringFromClass(cls);
    if (![name containsString:@"CandidateContentView"] &&
        ![name containsString:@"CandidatesSingleLineView"] &&
        ![name containsString:@"CandidatesVerticalContentView"] &&
        ![name containsString:@"StatusPill"] &&
        ![name containsString:@"AIQuestToolBar"] &&
        ![name containsString:@"Toolbar"]) return;
    Method update = class_getInstanceMethod(cls, @selector(updateLayer));
    if (update && !objc_getAssociatedObject(cls, WTOriginalUpdateKey)) {
        IMP original = method_getImplementation(update);
        IMP replacement = imp_implementationWithBlock(^(NSView *view) {
            ((void (*)(id, SEL))original)(view, @selector(updateLayer));
            wtApply(view);
        });
        // class_getInstanceMethod may return an inherited NSView method.
        // Add an override first so hooking a candidate cannot mutate NSView.
        if (!class_addMethod(cls, @selector(updateLayer), replacement, method_getTypeEncoding(update))) {
            method_setImplementation(class_getInstanceMethod(cls, @selector(updateLayer)), replacement);
        }
        objc_setAssociatedObject(cls, WTOriginalUpdateKey, @YES, OBJC_ASSOCIATION_RETAIN_NONATOMIC);
    }
}

static void wtViewMoved(id object, SEL selector) {
    Class cls = object_getClass(object);
    if (wtOriginalMove) ((void (*)(id, SEL))wtOriginalMove)(object, selector);
    NSView *view = object;
    if (wtIsSkinView(view)) {
        wtHookClass(cls);
        if (![wtViews containsObject:view]) NSLog(@"[WeTypeSkin] tracking %@", NSStringFromClass(cls));
        [wtViews addObject:view];
        wtApply(view);
    }
}

static void wtInstall(void) {
    wtViews = [NSHashTable weakObjectsHashTable];
    Method move = class_getInstanceMethod(NSView.class, @selector(viewDidMoveToWindow));
    if (move && !wtOriginalMove) {
        wtOriginalMove = method_getImplementation(move);
        method_setImplementation(move, (IMP)wtViewMoved);
    }
    wtTimer = dispatch_source_create(DISPATCH_SOURCE_TYPE_TIMER, 0, 0, dispatch_get_main_queue());
    dispatch_source_set_timer(wtTimer, dispatch_time(DISPATCH_TIME_NOW, 0), 500 * NSEC_PER_MSEC, 50 * NSEC_PER_MSEC);
    dispatch_source_set_event_handler(wtTimer, ^{
        (void)wtReadTheme();
        for (NSView *view in wtViews.allObjects) wtApply(view);
    });
    dispatch_resume(wtTimer);
}

__attribute__((constructor))
static void WeTypeSkinHookLoad(void) {
    fprintf(stderr, "[WeTypeSkin] loaded bundle=%s\n", NSBundle.mainBundle.bundleIdentifier.UTF8String ?: "missing");
    if (![NSBundle.mainBundle.bundleIdentifier isEqualToString:@"org.wetypeskinstudio.inputmethod.wetype"]) return;
    wtIsolateInputServer();
    dispatch_async(dispatch_get_main_queue(), ^{
        wtInstall();
    });
}
