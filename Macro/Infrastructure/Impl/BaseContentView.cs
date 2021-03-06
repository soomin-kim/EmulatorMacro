﻿using Macro.Extensions;
using Macro.Infrastructure.Manager;
using Macro.Infrastructure.Serialize;
using Macro.Models;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using Utils;
using Utils.Document;
using Utils.Extensions;
using Utils.Infrastructure;
using Point = System.Windows.Point;

namespace Macro.Infrastructure.Impl
{
    public abstract class BaseContentView : UserControl
    {
        private readonly Random _random;
        protected BaseContentView()
        {
            _random = new Random();
        }
        public abstract Task Save(object state);

        public abstract Task Delete(object state);

        public abstract bool Validate(IBaseEventTriggerModel model, out Message error);

        public abstract void Clear();
        public abstract Task Load(object state);
 
        public abstract IEnumerable<IBaseEventTriggerModel> GetEnumerator();

        public abstract Task<IBaseEventTriggerModel> InvokeNextEventTriggerAsync(IBaseEventTriggerModel saveModel, ProcessConfigModel processEventTriggerModel);

        //public async Task<IBaseEventTriggerModel> InvokeNextEventTriggerAsync(IBaseEventTriggerModel saveModel, ProcessConfigModel processEventTriggerModel)
        //{
        //    if (processEventTriggerModel.Token.IsCancellationRequested)
        //        return null;
        //    var nextModel = await TriggerProcess(saveModel, processEventTriggerModel);
        //    return nextModel.Item2;
        //}
        protected virtual void CaptureImage(Bitmap bmp)
        {
        }

        protected async Task<Tuple<bool, IBaseEventTriggerModel>> TriggerProcess<T>(T model, ProcessConfigModel processEventTriggerModel) where T : BaseEventTriggerModel<T>
        {
            var isExcute = false;

            var hWnd = IntPtr.Zero;
            var applciationData = ObjectExtensions.GetInstance<ApplicationDataManager>().Find(model.ProcessInfo.ProcessName) ?? new ApplicationDataModel();
            for (int i = 0; i < processEventTriggerModel.Processes.Count; ++i)
            {
                var factor = CalculateFactor(processEventTriggerModel.Processes[i].MainWindowHandle, model, applciationData.IsDynamic);

                if (string.IsNullOrEmpty(applciationData.HandleName))
                {
                    hWnd = processEventTriggerModel.Processes[i].MainWindowHandle;
                }
                else
                {
                    var item = NativeHelper.GetChildHandles(processEventTriggerModel.Processes[i].MainWindowHandle).Where(r => r.Item1.Equals(applciationData.HandleName)).FirstOrDefault();

                    if (item != null)
                        hWnd = item.Item2;
                    else
                        hWnd = processEventTriggerModel.Processes[i].MainWindowHandle;
                }

                if (model.RepeatInfo.RepeatType == RepeatType.Search && model.SubEventTriggers.Count > 0)
                {
                    var count = model.RepeatInfo.Count;
                    while (DisplayHelper.ProcessCapture(processEventTriggerModel.Processes[i], out Bitmap bmp, applciationData.IsDynamic) && count-- > 0)
                    {
                        var targetBmp = model.Image.Resize((int)Math.Truncate(model.Image.Width * factor.Item1.Item1), (int)Math.Truncate(model.Image.Height * factor.Item1.Item1));
                        var similarity = OpenCVHelper.Search(bmp, targetBmp, out Point location, processEventTriggerModel.SearchImageResultDisplay);
                        LogHelper.Debug($"RepeatType[Search : {count}] : >>>> Similarity : {similarity} % max Loc : X : {location.X} Y: {location.Y}");
                        CaptureImage(bmp);
                        if (!await TaskHelper.TokenCheckDelayAsync(model.AfterDelay, processEventTriggerModel.Token) || similarity > processEventTriggerModel.Similarity)
                            break;
                        for (int ii = 0; ii < model.SubEventTriggers.Count; ++ii)
                        {
                            await TriggerProcess(model.SubEventTriggers[ii], processEventTriggerModel);
                            if (processEventTriggerModel.Token.IsCancellationRequested)
                                break;
                        }
                        factor = CalculateFactor(processEventTriggerModel.Processes[i].MainWindowHandle, model, applciationData.IsDynamic);
                    }
                }
                else
                {
                    if (DisplayHelper.ProcessCapture(processEventTriggerModel.Processes[i], out Bitmap bmp, applciationData.IsDynamic))
                    {
                        var targetBmp = model.Image.Resize((int)Math.Truncate(model.Image.Width * factor.Item1.Item1), (int)Math.Truncate(model.Image.Height * factor.Item1.Item2));
                        var similarity = OpenCVHelper.Search(bmp, targetBmp, out Point location, processEventTriggerModel.SearchImageResultDisplay);
                        LogHelper.Debug($"Similarity : {similarity} % max Loc : X : {location.X} Y: {location.Y}");
                        CaptureImage(bmp);
                        if (similarity > processEventTriggerModel.Similarity)
                        {
                            if (model.SubEventTriggers.Count > 0)
                            {
                                if (model.RepeatInfo.RepeatType == RepeatType.Count || model.RepeatInfo.RepeatType == RepeatType.Once)
                                {
                                    for (int ii = 0; ii < model.RepeatInfo.Count; ++ii)
                                    {
                                        if (!await TaskHelper.TokenCheckDelayAsync(model.AfterDelay, processEventTriggerModel.Token))
                                            break;
                                        for (int iii = 0; iii < model.SubEventTriggers.Count; ++iii)
                                        {
                                            await TriggerProcess(model.SubEventTriggers[iii], processEventTriggerModel);
                                            if (processEventTriggerModel.Token.IsCancellationRequested)
                                                break;
                                        }
                                    }
                                }
                                else if (model.RepeatInfo.RepeatType == RepeatType.NoSearch)
                                {
                                    while (await TaskHelper.TokenCheckDelayAsync(model.AfterDelay, processEventTriggerModel.Token))
                                    {
                                        isExcute = false;
                                        for (int ii = 0; ii < model.SubEventTriggers.Count; ++ii)
                                        {
                                            var childResult = await TriggerProcess(model.SubEventTriggers[ii], processEventTriggerModel);
                                            if (processEventTriggerModel.Token.IsCancellationRequested)
                                            {
                                                break;
                                            }
                                            if (isExcute == false && childResult.Item1)
                                            {
                                                isExcute = childResult.Item1;
                                            }
                                        }
                                        if (!isExcute)
                                            break;
                                    }
                                }
                            }
                            else
                            {
                                isExcute = true;
                                if (model.EventType == EventType.Mouse)
                                {
                                    location.X = applciationData.OffsetX;
                                    location.Y = applciationData.OffsetY;
                                    MouseTriggerProcess(hWnd, location, model, factor.Item2);
                                }
                                else if (model.EventType == EventType.Image && model.SameImageDrag == true)
                                {
                                    location.X = ((location.X + applciationData.OffsetX) / factor.Item2.Item1) + (targetBmp.Width / factor.Item2.Item1 / 2);
                                    location.Y = ((location.Y + applciationData.OffsetY) / factor.Item2.Item2) + (targetBmp.Height / factor.Item2.Item2 / 2);

                                }
                                else if (model.EventType == EventType.Image)
                                {
                                    var percentage = _random.NextDouble();

                                    location.X = ((location.X + applciationData.OffsetX) / factor.Item2.Item1) + (targetBmp.Width / factor.Item2.Item1 * percentage);
                                    location.Y = ((location.Y + applciationData.OffsetY) / factor.Item2.Item2) + (targetBmp.Height / factor.Item2.Item1 * percentage);
                                    ImageTriggerProcess(hWnd, location, model);
                                }
                                else if (model.EventType == EventType.RelativeToImage)
                                {
                                    location.X = ((location.X + applciationData.OffsetX) / factor.Item2.Item1) + (targetBmp.Width / factor.Item2.Item1 / 2);
                                    location.Y = ((location.Y + applciationData.OffsetY) / factor.Item2.Item2) + (targetBmp.Height / factor.Item2.Item2 / 2);
                                    ImageTriggerProcess(hWnd, location, model);
                                }
                                
                                else if (model.EventType == EventType.Keyboard)
                                {
                                    KeyboardTriggerProcess(processEventTriggerModel.Processes[i].MainWindowHandle, model);
                                }
                                if (!await TaskHelper.TokenCheckDelayAsync(model.AfterDelay, processEventTriggerModel.Token))
                                    break;

                                if (model.EventToNext > 0 && model.TriggerIndex != model.EventToNext)
                                {
                                    IBaseEventTriggerModel nextModel = null;
                                    if(model is GameEventTriggerModel)
                                    {
                                        nextModel = ObjectExtensions.GetInstance<CacheDataManager>().GetGameEventTriggerModel(model.EventToNext);
                                    }
                                    else if(model is EventTriggerModel)
                                    {
                                        nextModel = ObjectExtensions.GetInstance<CacheDataManager>().GetEventTriggerModel(model.EventToNext);
                                    }

                                    if (nextModel != null)
                                    {
                                        LogHelper.Debug($">>>>Next Move Event : CurrentIndex [ {model.TriggerIndex} ] NextIndex [ {nextModel.TriggerIndex} ] ");
                                        return Tuple.Create(isExcute, nextModel);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            await TaskHelper.TokenCheckDelayAsync(processEventTriggerModel.ItemDelay, processEventTriggerModel.Token);
            return Tuple.Create<bool, IBaseEventTriggerModel>(isExcute, null);
        }

        protected Tuple<Tuple<float, float>, Tuple<float, float>> CalculateFactor(IntPtr hWnd, IBaseEventTriggerModel model, bool isDynamic)
        {
            var currentPosition = new Rect();
            NativeHelper.GetWindowRect(hWnd, ref currentPosition);
            var factor = NativeHelper.GetSystemDPI();
            var factorX = 1.0F;
            var factorY = 1.0F;
            var positionFactorX = 1.0F;
            var positionFactorY = 1.0F;
            if (isDynamic)
            {
                foreach (var monitor in DisplayHelper.MonitorInfo())
                {
                    if (monitor.Rect.IsContain(currentPosition))
                    {
                        factorX = factor.X * factorX / model.MonitorInfo.Dpi.X;
                        factorY = factor.Y * factorY / model.MonitorInfo.Dpi.Y;

                        if (model.EventType == EventType.Mouse)
                        {
                            positionFactorX = positionFactorX * monitor.Dpi.X / model.MonitorInfo.Dpi.X;
                            positionFactorY = positionFactorY * monitor.Dpi.Y / model.MonitorInfo.Dpi.Y;
                        }
                        else
                        {
                            positionFactorX = positionFactorX * factor.X / monitor.Dpi.X;
                            positionFactorY = positionFactorY * factor.Y / monitor.Dpi.Y;
                        }
                        break;
                    }
                }
            }
            return Tuple.Create(Tuple.Create(factorX, factorY), Tuple.Create(positionFactorX, positionFactorY));
        }
        private void KeyboardTriggerProcess(IntPtr hWnd, IBaseEventTriggerModel model)
        {
            var hWndActive = NativeHelper.GetForegroundWindow();
            Task.Delay(100).Wait();
            NativeHelper.SetForegroundWindow(hWnd);
            var inputs = model.KeyboardCmd.ToUpper().Split(new char[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            var modifiedKey = inputs.Where(r =>
            {
                if (Enum.TryParse($"{r}", out KeyCode keyCode))
                    return keyCode.IsExtendedKey();
                return false;
            }).Select(r =>
            {
                Enum.TryParse($"{r}", out KeyCode keyCode);
                return keyCode;
            }).ToArray();

            var command = new List<char>();
            foreach (var input in inputs)
            {
                if (Enum.TryParse(input, out KeyCode keyCode))
                {
                    if (!keyCode.IsExtendedKey())
                    {
                        for (int i = 0; i < input.Count(); i++)
                            command.Add(input[i]);
                    }
                }
                else
                {
                    for (int i = 0; i < input.Count(); i++)
                        command.Add(input[i]);
                }
            }
            var keys = command.Where(r =>
            {
                if (Enum.TryParse($"KEY_{r}", out KeyCode keyCode))
                    return !keyCode.IsExtendedKey();
                return false;
            }).Select(r =>
            {
                Enum.TryParse($"KEY_{r}", out KeyCode keyCode);
                return keyCode;
            }).ToArray();

            ObjectExtensions.GetInstance<InputManager>().Keyboard.ModifiedKeyStroke(modifiedKey, keys);
            Task.Delay(100).Wait();
            LogHelper.Debug($">>>>Keyboard Event");
            NativeHelper.SetForegroundWindow(hWndActive);
        }
        private void MouseTriggerProcess(IntPtr hWnd, Point location, IBaseEventTriggerModel model, Tuple<float, float> factor)
        {
            var mousePosition = new Point()
            {
                X = Math.Abs(model.ProcessInfo.Position.Left + (model.MouseTriggerInfo.StartPoint.X + location.X) * -1) * factor.Item1,
                Y = Math.Abs(model.ProcessInfo.Position.Top + (model.MouseTriggerInfo.StartPoint.Y + location.Y) * -1) * factor.Item2
            };

            if (model.MouseTriggerInfo.MouseInfoEventType == MouseEventType.LeftClick)
            {
                LogHelper.Debug($">>>>LMouse Save Position X : {model.MouseTriggerInfo.StartPoint.X} Save Position Y : {model.MouseTriggerInfo.StartPoint.Y} Target X : { mousePosition.X } Target Y : { mousePosition.Y }");
                NativeHelper.PostMessage(hWnd, WindowMessage.LButtonDown, 1, mousePosition.ToLParam());
                Task.Delay(100).Wait();
                NativeHelper.PostMessage(hWnd, WindowMessage.LButtonUp, 0, mousePosition.ToLParam());
            }
            else if (model.MouseTriggerInfo.MouseInfoEventType == MouseEventType.RightClick)
            {
                LogHelper.Debug($">>>>RMouse Save Position X : {model.MouseTriggerInfo.StartPoint.X} Save Position Y : {model.MouseTriggerInfo.StartPoint.Y} Target X : { mousePosition.X } Target Y : { mousePosition.Y }");
                NativeHelper.PostMessage(hWnd, WindowMessage.RButtonDown, 1, mousePosition.ToLParam());
                Task.Delay(100).Wait();
                NativeHelper.PostMessage(hWnd, WindowMessage.RButtonDown, 0, mousePosition.ToLParam());
            }
            else if (model.MouseTriggerInfo.MouseInfoEventType == MouseEventType.Drag)
            {
                LogHelper.Debug($">>>>Drag Mouse Save Position X : {model.MouseTriggerInfo.StartPoint.X} Save Position Y : {model.MouseTriggerInfo.StartPoint.Y} Target X : { mousePosition.X } Target Y : { mousePosition.Y }");
                NativeHelper.PostMessage(hWnd, WindowMessage.LButtonDown, 1, mousePosition.ToLParam());
                Task.Delay(100).Wait();
                for (int i = 0; i < model.MouseTriggerInfo.MiddlePoint.Count; ++i)
                {
                    mousePosition = new Point()
                    {
                        X = Math.Abs(model.ProcessInfo.Position.Left + model.MouseTriggerInfo.MiddlePoint[i].X * -1) * factor.Item1,
                        Y = Math.Abs(model.ProcessInfo.Position.Top + model.MouseTriggerInfo.MiddlePoint[i].Y * -1) * factor.Item2
                    };
                    NativeHelper.PostMessage(hWnd, WindowMessage.MouseMove, 1, mousePosition.ToLParam());
                    Task.Delay(100).Wait();
                }
                mousePosition = new Point()
                {
                    X = Math.Abs(model.ProcessInfo.Position.Left + model.MouseTriggerInfo.EndPoint.X * -1) * factor.Item1,
                    Y = Math.Abs(model.ProcessInfo.Position.Top + model.MouseTriggerInfo.EndPoint.Y * -1) * factor.Item2
                };
                NativeHelper.PostMessage(hWnd, WindowMessage.MouseMove, 1, mousePosition.ToLParam());
                Task.Delay(100).Wait();
                NativeHelper.PostMessage(hWnd, WindowMessage.LButtonUp, 0, mousePosition.ToLParam());
                LogHelper.Debug($">>>>Drag Mouse Save Position X : {model.MouseTriggerInfo.EndPoint.X} Save Position Y : {model.MouseTriggerInfo.EndPoint.Y} Target X : { mousePosition.X } Target Y : { mousePosition.Y }");
            }
            else if (model.MouseTriggerInfo.MouseInfoEventType == MouseEventType.Wheel)
            {
                LogHelper.Debug($">>>>Wheel Save Position X : {model.MouseTriggerInfo.StartPoint.X} Save Position Y : {model.MouseTriggerInfo.StartPoint.Y} Target X : { mousePosition.X } Target Y : { mousePosition.Y }");
                //NativeHelper.PostMessage(hWnd, WindowMessage.LButtonDown, 1, mousePosition.ToLParam());
                //Task.Delay(100).Wait();
                //NativeHelper.PostMessage(hWnd, WindowMessage.LButtonUp, 0, mousePosition.ToLParam());
                //NativeHelper.PostMessage(hWnd, WindowMessage.MouseWheel, ObjectExtensions.MakeWParam((uint)WindowMessage.MKControl, (uint)(model.MouseTriggerInfo.WheelData * -1)), 0);
                //var hwnd = NativeHelper.FindWindowEx(NativeHelper.FindWindow(null, "Test.txt - 메모장"), IntPtr.Zero, "Edit", null);
                //var p = new System.Drawing.Point(0, 0);
                NativeHelper.PostMessage(hWnd, WindowMessage.MouseWheel, ObjectExtensions.MakeWParam(0, model.MouseTriggerInfo.WheelData * ConstHelper.WheelDelta), mousePosition.ToLParam());
            }
        }
        private void ImageTriggerProcess(IntPtr hWnd, Point location, IBaseEventTriggerModel model)
        {
            var position = new Point()
            {
                X = location.X + model.MouseTriggerInfo.StartPoint.X,
                Y = location.Y + model.MouseTriggerInfo.StartPoint.Y
            };

            LogHelper.Debug($">>>>Image Location X : {position.X} Location Y : {position.Y}");
            NativeHelper.PostMessage(hWnd, WindowMessage.LButtonDown, 1, position.ToLParam());
            Task.Delay(100).Wait();
            NativeHelper.PostMessage(hWnd, WindowMessage.LButtonUp, 0, position.ToLParam());
        }
        private void ImageDragToParent(IntPtr hWnd, Point parentLocation, Point currentLocation)
        {

        }
    }
}
